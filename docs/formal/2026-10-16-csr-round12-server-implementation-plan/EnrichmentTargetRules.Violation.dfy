// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T5 S3
// Source: Iverson.Server/Iverson.Api/Schema/EnrichmentTargetRules.cs `Violation`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/EnrichmentTargetRules.Violation.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

// C# string: UTF-16 code units; Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

// C# nullable reference.
datatype Option<T> = None | Some(value: T)

// ---------------------------------------------------------------------------
// Approved axioms (library boundary)
//   OrdinalUpper(s)     the per-code-unit invariant upper-casing OrdinalIgnoreCase compares under
//   CharIsWhiteSpace(c) char.IsWhiteSpace(c)
// ---------------------------------------------------------------------------

function OrdinalUpper(s: seq<char>): seq<char>

predicate CharIsWhiteSpace(c: char)

// StringComparison.OrdinalIgnoreCase / StringComparer.OrdinalIgnoreCase.
predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

// string.IsNullOrWhiteSpace.
predicate NullOrWhiteSpace(h: Option<CsString>) { h.None? || forall i :: 0 <= i < |h.value| ==> CharIsWhiteSpace(h.value[i]) }

// string.IsNullOrEmpty.
predicate NullOrEmpty(h: Option<CsString>) { h.None? || |h.value| == 0 }

// ---------------------------------------------------------------------------
// Schema records (SchemaDescriptor.cs)
// ---------------------------------------------------------------------------

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
datatype Desc = Desc(
  TypeName: CsString,
  TableName: CsString,
  KeyColumn: Column,
  ScalarColumns: seq<Column>,
  VectorFields: seq<VectorField>,
  ChunkFields: seq<ChunkField>,
  Relations: seq<Relation>,
  Authorization: Option<AuthRules>,
  TenantColumn: CsString,
  PopularitySignalColumn: Option<CsString>,
  OwnerTenantId: Option<CsString>,
  EnrichmentTargets: seq<Target>,
  HasDocumentTemplate: bool)

// ---------------------------------------------------------------------------
// Transliteration of EnrichmentTargetRules.Violation
// ---------------------------------------------------------------------------

// private const string SyntheticDocumentField = "Document";
const SyntheticDocumentField: seq<char> := "Document"

// HashSet<string>(StringComparer.OrdinalIgnoreCase).Contains(n): some element equals n under
// the comparer. The set is kept as the sequence it was built from; Contains and Count == 0 do
// not depend on deduplication.
predicate ContainsIC(xs: seq<CsString>, n: seq<char>) { exists x :: x in xs && EqIC(x, n) }

// descriptor.VectorFields.Select(v => v.PropertyName)
function VNames(vs: seq<VectorField>): seq<CsString>
{
  if |vs| == 0 then [] else [vs[0].PropertyName] + VNames(vs[1..])
}

// descriptor.ChunkFields.Select(c => c.PropertyName)
//   .Where(name => descriptor.DocumentTemplate is null ||
//                  !string.Equals(name, SyntheticDocumentField, StringComparison.Ordinal))
function CNames(cs: seq<ChunkField>, hasTemplate: bool): seq<CsString>
{
  if |cs| == 0 then []
  else
    var name := cs[0].PropertyName;
    (if !hasTemplate || !(name == SyntheticDocumentField) then [name] else []) + CNames(cs[1..], hasTemplate)
}

// sourceNames (lines 35-40): Select(...).Concat(...).ToHashSet(OrdinalIgnoreCase)
function SourceNames(d: Desc): seq<CsString>
{
  VNames(d.VectorFields) + CNames(d.ChunkFields, d.HasDocumentTemplate)
}

// descriptor.ScalarColumns.FirstOrDefault(c => string.Equals(c.Name, name, OrdinalIgnoreCase))
function FirstMatchingColumn(cols: seq<Column>, name: seq<char>): Option<Column>
{
  if |cols| == 0 then None
  else if EqIC(cols[0].Name, name) then Some(cols[0])
  else FirstMatchingColumn(cols[1..], name)
}

// lines 47-51: scalar lookup ?? (key matches ? KeyColumn : null)
function ColumnOf(d: Desc, t: Target): Option<Column>
{
  match FirstMatchingColumn(d.ScalarColumns, t.ColumnName)
  case Some(c) => Some(c)
  case None => if EqIC(d.KeyColumn.Name, t.ColumnName) then Some(d.KeyColumn) else None
}

// line 30: descriptor.Authorization?.OwnerField
function OwnerFieldOf(d: Desc): Option<CsString>
{
  if d.Authorization.Some? then d.Authorization.value.OwnerField else None
}

// lines 47-93: the body of the first foreach, for one target.
function TargetCheck(d: Desc, ownerField: Option<CsString>, sourceNames: seq<CsString>, t: Target): Option<seq<char>>
{
  var column := ColumnOf(d, t);
  if column.None? then
    Some("Enrichment target '" + t.ColumnName + "' on '" + d.TypeName + "' is not a column " +
         "of the type.")
  else if !EqIC(column.value.SqlType, "TEXT") && !EqIC(column.value.SqlType, "TEXT[]") then
    Some("Enrichment target '" + t.ColumnName + "' on '" + d.TypeName + "' must be a string " +
         "property; its column type is '" + column.value.SqlType + "'.")
  else if EqIC(t.ColumnName, d.KeyColumn.Name) ||
          EqIC(t.ColumnName, d.TenantColumn) ||
          (!NullOrEmpty(ownerField) && EqIC(t.ColumnName, ownerField.value)) then
    Some("Enrichment target '" + t.ColumnName + "' on '" + d.TypeName + "' cannot be the key " +
         "or owner_field.")
  else if ContainsIC(sourceNames, t.ColumnName) then
    Some("Enrichment target '" + t.ColumnName + "' on '" + d.TypeName + "' cannot also carry " +
         "[IversonEmbedding]/[IversonChunk]: if this property were also a source property, the " +
         "enrichment writeback would mutate the hashed text, causing the enricher to re-enrich its " +
         "own republished event without bound.")
  else if t.Kind == Extracted && NullOrWhiteSpace(t.Hint) then
    Some("Enrichment target '" + t.ColumnName + "' on '" + d.TypeName + "' has [IversonExtracted] " +
         "with an empty hint.")
  else
    None
}

// line 42: foreach (var target in descriptor.EnrichmentTargets) { ... return ...; }
function TargetLoop(d: Desc, ownerField: Option<CsString>, sourceNames: seq<CsString>, ts: seq<Target>): Option<seq<char>>
{
  if |ts| == 0 then None
  else
    var r := TargetCheck(d, ownerField, sourceNames, ts[0]);
    if r.Some? then r else TargetLoop(d, ownerField, sourceNames, ts[1..])
}

// lines 104-106: EnrichmentTargets.Select(t => t.ColumnName).ToHashSet(OrdinalIgnoreCase)
function TargetNames(ts: seq<Target>): seq<CsString>
{
  if |ts| == 0 then [] else [ts[0].ColumnName] + TargetNames(ts[1..])
}

// lines 116-117: fieldPermissions.FirstOrDefault(fp => fp.WritableRoles.Count > 0 &&
//                                                       targetNames.Contains(fp.FieldName))
function FirstWriteRestricted(fps: seq<FieldPermission>, targetNames: seq<CsString>): Option<FieldPermission>
{
  if |fps| == 0 then None
  else if |fps[0].WritableRoles| > 0 && ContainsIC(targetNames, fps[0].FieldName) then Some(fps[0])
  else FirstWriteRestricted(fps[1..], targetNames)
}

// lines 134-137: fieldPermissions.Any(fp => fp.ReadableRoles.Count > 0 &&
//     string.Equals(fp.FieldName, target.ColumnName, OrdinalIgnoreCase) &&
//     fp.ReadableRoles.All(role => source.ReadableRoles.Contains(role, StringComparer.Ordinal)))
predicate Covered(fps: seq<FieldPermission>, source: FieldPermission, t: Target)
{
  exists fp :: fp in fps && |fp.ReadableRoles| > 0 &&
    EqIC(fp.FieldName, t.ColumnName) &&
    (forall role :: role in fp.ReadableRoles ==> role in source.ReadableRoles)
}

// lines 132-147: inner foreach over the targets for one restricted source permission.
function InnerLoop(d: Desc, fps: seq<FieldPermission>, source: FieldPermission, ts: seq<Target>): Option<seq<char>>
{
  if |ts| == 0 then None
  else if !Covered(fps, source, ts[0]) then
    Some("Enrichment target '" + ts[0].ColumnName + "' on '" + d.TypeName + "' is generated from " +
         "source property '" + source.FieldName + "', whose readable_roles are restricted, but the " +
         "target carries no FieldPermission whose readable_roles are a subset of them. Every " +
         "caller who can read an enrichment target must be able to read every source it is " +
         "generated from.")
  else InnerLoop(d, fps, source, ts[1..])
}

// lines 129-148: foreach (var source in fieldPermissions.Where(fp =>
//     fp.ReadableRoles.Count > 0 && sourceNames.Contains(fp.FieldName)))
function ReadLoop(d: Desc, sourceNames: seq<CsString>, fps: seq<FieldPermission>, rest: seq<FieldPermission>): Option<seq<char>>
{
  if |rest| == 0 then None
  else if !(|rest[0].ReadableRoles| > 0 && ContainsIC(sourceNames, rest[0].FieldName)) then
    ReadLoop(d, sourceNames, fps, rest[1..])
  else
    var r := InnerLoop(d, fps, rest[0], d.EnrichmentTargets);
    if r.Some? then r else ReadLoop(d, sourceNames, fps, rest[1..])
}

// EnrichmentTargetRules.Violation (lines 25-151).
function Violation(d: Desc): Option<seq<char>>
{
  if |d.EnrichmentTargets| == 0 then None
  else
    var ownerField := OwnerFieldOf(d);
    var sourceNames := SourceNames(d);
    var r := TargetLoop(d, ownerField, sourceNames, d.EnrichmentTargets);
    if r.Some? then r
    else if |sourceNames| == 0 then
      Some("'" + d.TypeName + "' declares enrichment targets but has no source text property " +
           "for the enrichment pipeline to read from.")
    else
      var targetNames := TargetNames(d.EnrichmentTargets);
      var fieldPermissions := if d.Authorization.Some? then d.Authorization.value.FieldPermissions else [];
      var writeRestricted := FirstWriteRestricted(fieldPermissions, targetNames);
      if writeRestricted.Some? then
        Some("Enrichment target '" + writeRestricted.value.FieldName + "' on '" + d.TypeName + "' carries a " +
             "FieldPermission with writable_roles. The enrichment pipeline is the target's only writer, so " +
             "a write restriction on it cannot be enforced; remove writable_roles from that FieldPermission.")
      else
        ReadLoop(d, sourceNames, fieldPermissions, fieldPermissions)
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

ghost function SrcSet(d: Desc): set<CsString>
{
  (set v | v in d.VectorFields :: v.PropertyName) +
  (set c | c in d.ChunkFields && !(d.HasDocumentTemplate && c.PropertyName == "Document") :: c.PropertyName)
}

lemma VNamesMembers(vs: seq<VectorField>)
  ensures forall x :: x in VNames(vs) <==> exists v :: v in vs && v.PropertyName == x
{
  if |vs| > 0 {
    VNamesMembers(vs[1..]);
    assert vs == [vs[0]] + vs[1..];
  }
}

lemma CNamesMembers(cs: seq<ChunkField>, h: bool)
  ensures forall x :: x in CNames(cs, h) <==>
    exists c :: c in cs && !(h && c.PropertyName == "Document") && c.PropertyName == x
{
  if |cs| > 0 {
    CNamesMembers(cs[1..], h);
    assert cs == [cs[0]] + cs[1..];
  }
}

lemma SourceNamesIsSrcSet(d: Desc)
  ensures forall x :: x in SourceNames(d) <==> x in SrcSet(d)
  ensures |SourceNames(d)| == 0 <==> SrcSet(d) == {}
{
  VNamesMembers(d.VectorFields);
  CNamesMembers(d.ChunkFields, d.HasDocumentTemplate);
  if |SourceNames(d)| > 0 {
    var x := SourceNames(d)[0];
    assert x in SourceNames(d);
    assert x in SrcSet(d);
  }
  if SrcSet(d) != {} {
    var x :| x in SrcSet(d);
    assert x in SourceNames(d);
  }
}

lemma TargetNamesMembers(ts: seq<Target>)
  ensures forall t :: t in ts ==> t.ColumnName in TargetNames(ts)
  ensures forall n :: n in TargetNames(ts) ==> exists t :: t in ts && t.ColumnName == n
{
  if |ts| > 0 {
    TargetNamesMembers(ts[1..]);
    assert ts == [ts[0]] + ts[1..];
  }
}

lemma FirstMatchingColumnSpec(cols: seq<Column>, name: seq<char>)
  ensures FirstMatchingColumn(cols, name).Some? ==>
    FirstMatchingColumn(cols, name).value in cols && EqIC(FirstMatchingColumn(cols, name).value.Name, name)
  ensures FirstMatchingColumn(cols, name).None? <==> forall c :: c in cols ==> !EqIC(c.Name, name)
{
  if |cols| > 0 {
    FirstMatchingColumnSpec(cols[1..], name);
    assert cols == [cols[0]] + cols[1..];
  }
}

lemma TargetLoopNone(d: Desc, o: Option<CsString>, src: seq<CsString>, ts: seq<Target>)
  ensures TargetLoop(d, o, src, ts).None? <==> forall t :: t in ts ==> TargetCheck(d, o, src, t).None?
{
  if |ts| > 0 {
    TargetLoopNone(d, o, src, ts[1..]);
    assert ts == [ts[0]] + ts[1..];
  }
}

lemma FirstWriteRestrictedNone(fps: seq<FieldPermission>, tn: seq<CsString>)
  ensures FirstWriteRestricted(fps, tn).None? <==>
    forall fp :: fp in fps ==> !(|fp.WritableRoles| > 0 && ContainsIC(tn, fp.FieldName))
{
  if |fps| > 0 {
    FirstWriteRestrictedNone(fps[1..], tn);
    assert fps == [fps[0]] + fps[1..];
  }
}

lemma InnerLoopNone(d: Desc, fps: seq<FieldPermission>, s: FieldPermission, ts: seq<Target>)
  ensures InnerLoop(d, fps, s, ts).None? <==> forall t :: t in ts ==> Covered(fps, s, t)
{
  if |ts| > 0 {
    InnerLoopNone(d, fps, s, ts[1..]);
    assert ts == [ts[0]] + ts[1..];
  }
}

lemma ReadLoopNone(d: Desc, src: seq<CsString>, fps: seq<FieldPermission>, rest: seq<FieldPermission>)
  ensures ReadLoop(d, src, fps, rest).None? <==>
    forall s :: s in rest && |s.ReadableRoles| > 0 && ContainsIC(src, s.FieldName) ==>
      forall t :: t in d.EnrichmentTargets ==> Covered(fps, s, t)
{
  if |rest| > 0 {
    ReadLoopNone(d, src, fps, rest[1..]);
    InnerLoopNone(d, fps, rest[0], d.EnrichmentTargets);
    assert rest == [rest[0]] + rest[1..];
  }
}

// Everything Violation(d).None? implies, stage by stage.
lemma NoneFacts(d: Desc)
  requires Violation(d).None?
  ensures forall t :: t in d.EnrichmentTargets ==> TargetCheck(d, OwnerFieldOf(d), SourceNames(d), t).None?
  ensures |d.EnrichmentTargets| > 0 ==> |SourceNames(d)| > 0
  ensures d.Authorization.Some? ==>
    forall fp :: fp in d.Authorization.value.FieldPermissions ==>
      !(|fp.WritableRoles| > 0 && ContainsIC(TargetNames(d.EnrichmentTargets), fp.FieldName))
  ensures d.Authorization.Some? ==>
    forall s :: s in d.Authorization.value.FieldPermissions && |s.ReadableRoles| > 0 && ContainsIC(SourceNames(d), s.FieldName) ==>
      forall t :: t in d.EnrichmentTargets ==> Covered(d.Authorization.value.FieldPermissions, s, t)
{
  if |d.EnrichmentTargets| > 0 {
    var fps := if d.Authorization.Some? then d.Authorization.value.FieldPermissions else [];
    TargetLoopNone(d, OwnerFieldOf(d), SourceNames(d), d.EnrichmentTargets);
    FirstWriteRestrictedNone(fps, TargetNames(d.EnrichmentTargets));
    ReadLoopNone(d, SourceNames(d), fps, fps);
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "The whole rule set in `SchemaRegistrationOrchestrator.ValidateEnrichmentTargets` (target types, sources, write restrictions and read-role subsets) is restated as a pure predicate over `SchemaDescriptor`: TEXT and TEXT[] targets, with the synthetic `Document` excluded." (spec §4)
lemma P1_AdmittedTargetsAreTextOrTextArrayColumns(d: Desc)
  requires Violation(d).None?
  ensures forall t :: t in d.EnrichmentTargets ==>
    exists c :: (c in d.ScalarColumns || c == d.KeyColumn) && EqIC(c.Name, t.ColumnName) &&
      (EqIC(c.SqlType, "TEXT") || EqIC(c.SqlType, "TEXT[]"))
{
  NoneFacts(d);
  forall t | t in d.EnrichmentTargets
    ensures exists c :: ((c in d.ScalarColumns || c == d.KeyColumn) && EqIC(c.Name, t.ColumnName) &&
                         (EqIC(c.SqlType, "TEXT") || EqIC(c.SqlType, "TEXT[]")))
  {
    assert TargetCheck(d, OwnerFieldOf(d), SourceNames(d), t).None?;
    FirstMatchingColumnSpec(d.ScalarColumns, t.ColumnName);
    var c := ColumnOf(d, t).value;
    assert (c in d.ScalarColumns || c == d.KeyColumn) && EqIC(c.Name, t.ColumnName);
  }
}

// Citation: "The whole rule set in `SchemaRegistrationOrchestrator.ValidateEnrichmentTargets` (target types, sources, write restrictions and read-role subsets) is restated as a pure predicate over `SchemaDescriptor`" (spec §4) and "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, `LoadAsync_DescriptorBreakingAnEnrichmentRule_LoadsWithNoTargetsAndLogsError`, cases "target-is-the-key" and "target-is-the-tenant-column")
lemma P2_AdmittedTargetsAreNotTheKeyTheTenantOrTheOwner(d: Desc)
  requires Violation(d).None?
  ensures forall t :: t in d.EnrichmentTargets ==>
    !EqIC(t.ColumnName, d.KeyColumn.Name) && !EqIC(t.ColumnName, d.TenantColumn) &&
    (d.Authorization.Some? && d.Authorization.value.OwnerField.Some? && |d.Authorization.value.OwnerField.value| > 0 ==>
      !EqIC(t.ColumnName, d.Authorization.value.OwnerField.value))
{
  NoneFacts(d);
  forall t | t in d.EnrichmentTargets
    ensures !EqIC(t.ColumnName, d.KeyColumn.Name) && !EqIC(t.ColumnName, d.TenantColumn) &&
      (d.Authorization.Some? && d.Authorization.value.OwnerField.Some? && |d.Authorization.value.OwnerField.value| > 0 ==>
        !EqIC(t.ColumnName, d.Authorization.value.OwnerField.value))
  {
    assert TargetCheck(d, OwnerFieldOf(d), SourceNames(d), t).None?;
  }
}

// Citation: "TEXT and TEXT[] targets, with the synthetic `Document` excluded." (spec §4) and "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, cases "target-is-a-source" and "only-source-is-the-synthetic-document")
lemma P3_AdmittedTargetsHaveASourceAndAreNotSources(d: Desc)
  requires Violation(d).None? && |d.EnrichmentTargets| > 0
  ensures var srcs := (set v | v in d.VectorFields :: v.PropertyName) +
                      (set c | c in d.ChunkFields && !(d.HasDocumentTemplate && c.PropertyName == "Document") :: c.PropertyName);
    srcs != {} &&
    forall t, s :: t in d.EnrichmentTargets && s in srcs ==> !EqIC(s, t.ColumnName)
{
  NoneFacts(d);
  SourceNamesIsSrcSet(d);
  var srcs := (set v | v in d.VectorFields :: v.PropertyName) +
              (set c | c in d.ChunkFields && !(d.HasDocumentTemplate && c.PropertyName == "Document") :: c.PropertyName);
  assert srcs == SrcSet(d);
  forall t, s | t in d.EnrichmentTargets && s in srcs
    ensures !EqIC(s, t.ColumnName)
  {
    assert TargetCheck(d, OwnerFieldOf(d), SourceNames(d), t).None?;
    assert s in SourceNames(d);
  }
}

// Citation: "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, case "extracted-no-hint")
lemma P4_AdmittedExtractedTargetsHaveAHint(d: Desc)
  requires Violation(d).None?
  ensures forall t :: t in d.EnrichmentTargets && t.Kind == Extracted ==> !NullOrWhiteSpace(t.Hint)
{
  NoneFacts(d);
  forall t | t in d.EnrichmentTargets && t.Kind == Extracted
    ensures !NullOrWhiteSpace(t.Hint)
  {
    assert TargetCheck(d, OwnerFieldOf(d), SourceNames(d), t).None?;
  }
}

// Citation: "(target types, sources, write restrictions and read-role subsets)" (spec §4) and "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, case "target-write-restricted")
lemma P5_NoAdmittedTargetIsWriteRestricted(d: Desc)
  requires Violation(d).None?
  ensures d.Authorization.Some? ==>
    forall fp, t :: fp in d.Authorization.value.FieldPermissions && t in d.EnrichmentTargets && |fp.WritableRoles| > 0 ==>
      !EqIC(fp.FieldName, t.ColumnName)
{
  NoneFacts(d);
  TargetNamesMembers(d.EnrichmentTargets);
  if d.Authorization.Some? {
    forall fp, t | fp in d.Authorization.value.FieldPermissions && t in d.EnrichmentTargets && |fp.WritableRoles| > 0
      ensures !EqIC(fp.FieldName, t.ColumnName)
    {
      assert t.ColumnName in TargetNames(d.EnrichmentTargets);
      assert !ContainsIC(TargetNames(d.EnrichmentTargets), fp.FieldName);
      assert !EqIC(t.ColumnName, fp.FieldName);
    }
  }
}

// Citation: "(target types, sources, write restrictions and read-role subsets)" (spec §4) and "loaded.EnrichmentTargets.Should().BeEmpty();" (plan T5 S1a, case "target-more-readable-than-its-source")
lemma P6_EveryTargetReaderCanReadEverySource(d: Desc)
  requires Violation(d).None?
  ensures var srcs := (set v | v in d.VectorFields :: v.PropertyName) +
                      (set c | c in d.ChunkFields && !(d.HasDocumentTemplate && c.PropertyName == "Document") :: c.PropertyName);
    d.Authorization.Some? ==>
      forall s, t :: s in d.Authorization.value.FieldPermissions && |s.ReadableRoles| > 0 &&
          (exists n :: n in srcs && EqIC(n, s.FieldName)) && t in d.EnrichmentTargets ==>
        exists f :: f in d.Authorization.value.FieldPermissions && |f.ReadableRoles| > 0 &&
          EqIC(f.FieldName, t.ColumnName) && forall r :: r in f.ReadableRoles ==> r in s.ReadableRoles
{
  NoneFacts(d);
  SourceNamesIsSrcSet(d);
  var srcs := (set v | v in d.VectorFields :: v.PropertyName) +
              (set c | c in d.ChunkFields && !(d.HasDocumentTemplate && c.PropertyName == "Document") :: c.PropertyName);
  assert srcs == SrcSet(d);
  if d.Authorization.Some? {
    var fps := d.Authorization.value.FieldPermissions;
    forall s, t | s in fps && |s.ReadableRoles| > 0 &&
        (exists n :: n in srcs && EqIC(n, s.FieldName)) && t in d.EnrichmentTargets
      ensures exists f :: (f in fps && |f.ReadableRoles| > 0 &&
                           EqIC(f.FieldName, t.ColumnName) && forall r :: r in f.ReadableRoles ==> r in s.ReadableRoles)
    {
      var n :| n in srcs && EqIC(n, s.FieldName);
      assert n in SourceNames(d);
      assert ContainsIC(SourceNames(d), s.FieldName);
      assert Covered(fps, s, t);
    }
  }
}

// Citation: "_sut.Get("Article")!.EnrichmentTargets.Should().BeEquivalentTo(stored.EnrichmentTargets);" (plan T5 S1a, `LoadAsync_DescriptorKeepingTheEnrichmentRules_KeepsItsTargets`, cases "text", "text-array" and "template-beside-a-real-source", generalised) and "TEXT and TEXT[] targets, with the synthetic `Document` excluded." (spec §4)
lemma P7_ADescriptorKeepingEveryRuleIsAdmitted(d: Desc)
  requires var srcs := (set v | v in d.VectorFields :: v.PropertyName) +
                       (set c | c in d.ChunkFields && !(d.HasDocumentTemplate && c.PropertyName == "Document") :: c.PropertyName);
    (forall t :: t in d.EnrichmentTargets ==>
      (exists c :: c in d.ScalarColumns && EqIC(c.Name, t.ColumnName)) &&
      (forall c :: c in d.ScalarColumns && EqIC(c.Name, t.ColumnName) ==> EqIC(c.SqlType, "TEXT") || EqIC(c.SqlType, "TEXT[]")) &&
      !EqIC(t.ColumnName, d.KeyColumn.Name) && !EqIC(t.ColumnName, d.TenantColumn) &&
      (d.Authorization.Some? && d.Authorization.value.OwnerField.Some? && |d.Authorization.value.OwnerField.value| > 0 ==>
        !EqIC(t.ColumnName, d.Authorization.value.OwnerField.value)) &&
      (forall s :: s in srcs ==> !EqIC(s, t.ColumnName)) &&
      (t.Kind == Extracted ==> !NullOrWhiteSpace(t.Hint))) &&
    (|d.EnrichmentTargets| > 0 ==> srcs != {}) &&
    (d.Authorization.Some? ==>
      (forall fp, t :: fp in d.Authorization.value.FieldPermissions && t in d.EnrichmentTargets && |fp.WritableRoles| > 0 ==>
        !EqIC(fp.FieldName, t.ColumnName)) &&
      (forall s, t :: s in d.Authorization.value.FieldPermissions && |s.ReadableRoles| > 0 &&
          (exists n :: n in srcs && EqIC(n, s.FieldName)) && t in d.EnrichmentTargets ==>
        exists f :: f in d.Authorization.value.FieldPermissions && |f.ReadableRoles| > 0 &&
          EqIC(f.FieldName, t.ColumnName) && forall r :: r in f.ReadableRoles ==> r in s.ReadableRoles))
  ensures Violation(d).None?
{
  if |d.EnrichmentTargets| > 0 {
    var srcs := (set v | v in d.VectorFields :: v.PropertyName) +
                (set c | c in d.ChunkFields && !(d.HasDocumentTemplate && c.PropertyName == "Document") :: c.PropertyName);
    assert srcs == SrcSet(d);
    SourceNamesIsSrcSet(d);
    TargetNamesMembers(d.EnrichmentTargets);
    var o := OwnerFieldOf(d);
    var src := SourceNames(d);
    var fps := if d.Authorization.Some? then d.Authorization.value.FieldPermissions else [];

    forall t | t in d.EnrichmentTargets
      ensures TargetCheck(d, o, src, t).None?
    {
      FirstMatchingColumnSpec(d.ScalarColumns, t.ColumnName);
      assert ColumnOf(d, t).Some?;
      assert ColumnOf(d, t).value in d.ScalarColumns;
      forall x | x in src
        ensures !EqIC(x, t.ColumnName)
      {
        assert x in srcs;
      }
      assert !ContainsIC(src, t.ColumnName);
    }
    TargetLoopNone(d, o, src, d.EnrichmentTargets);
    assert TargetLoop(d, o, src, d.EnrichmentTargets).None?;
    assert |src| > 0;

    var tn := TargetNames(d.EnrichmentTargets);
    forall fp | fp in fps && |fp.WritableRoles| > 0
      ensures !ContainsIC(tn, fp.FieldName)
    {
      forall n | n in tn
        ensures !EqIC(n, fp.FieldName)
      {
        var t :| t in d.EnrichmentTargets && t.ColumnName == n;
        assert !EqIC(fp.FieldName, t.ColumnName);
      }
    }
    FirstWriteRestrictedNone(fps, tn);

    forall s | s in fps && |s.ReadableRoles| > 0 && ContainsIC(src, s.FieldName)
      ensures forall t :: t in d.EnrichmentTargets ==> Covered(fps, s, t)
    {
      var n :| n in src && EqIC(n, s.FieldName);
      assert n in srcs;
    }
    ReadLoopNone(d, src, fps, fps);
  }
}
