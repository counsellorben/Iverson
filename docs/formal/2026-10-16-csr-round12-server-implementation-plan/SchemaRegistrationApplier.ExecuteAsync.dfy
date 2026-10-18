// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T7 S3
// Source: Iverson.Server/Iverson.Api/PrivilegedRequests/SchemaRegistrationApplier.cs `ExecuteAsync`, `RequireAdoptableAsync`, `NotAdoptable`, `OwnedByAnotherTenant`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/SchemaRegistrationApplier.ExecuteAsync.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

// SchemaDescriptor, reduced to the fields this candidate reads. KeyColumn/ScalarColumns feed only
// SchemaBuilder.ToTableSchema, whose DDL outcome the oracle `ddl` keys by table name.
datatype Desc = Desc(TypeName: CsString, TableName: CsString, TenantColumn: CsString, OwnerTenantId: Option<CsString>)

// ---------------------------------------------------------------------------
// Candidate model: the stores as an oracle
// ---------------------------------------------------------------------------

// Iverson.Sql/ExistingTableInspector.cs: record ExistingTable(bool IsOrdinaryTable, bool HasTenantColumn)
datatype ExistingTable = ExistingTable(IsOrdinaryTable: bool, HasTenantColumn: bool)

// ApplySchemaAsync(..., SchemaDriftPolicy.Throw): returns, throws SchemaDriftException(message), or throws otherwise.
datatype DdlResult = Applied | Drift(message: seq<char>) | DdlFault

datatype Db = Db(
  owners: CsString -> seq<Option<CsString>>,        // registryRepository.GetOwnersAsync(typeName)
  exists_: CsString -> bool,                         // registryRepository.ExistsAsync(typeName)
  describe: CsString -> Option<ExistingTable>,       // tables.DescribeAsync(tableName, tenantColumn)
  foreignRows: (CsString, CsString) -> bool,         // tables.HasRowsOfAnotherTenantAsync(tableName, tenantColumn, owner)
  ddl: CsString -> DdlResult,                        // schemaManager.ApplySchemaAsync(ToTableSchema(d), Throw)
  upsert: (CsString, Option<CsString>) -> bool)      // registryRepository.UpsertIfOwnerAsync(typeName, json, owner)

datatype Effect = Ddl(table: CsString) | Write(typeName: CsString, owner: Option<CsString>) | Enqueue(typeName: CsString)

datatype Outcome = Done | Rpc(code: StatusCode, detail: seq<char>) | Fault

// Approved axiom: JsonSerializer.Serialize(descriptor, SchemaRegistry.JsonOptions)
function SerializeDescriptor(d: Desc): CsString

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// PrivilegedRequestContracts.cs:21-23 (SchemaRegistrationRefusals.OwnedByAnotherTenant; moved out of
// SchemaRegistrationApplier.cs as built, body unchanged)
function OwnedByAnotherTenant(typeName: CsString): Outcome
{
  Rpc(PermissionDenied, "Type '" + typeName + "' is registered to another tenant and cannot be re-registered here.")
}

// SchemaRegistrationApplier.cs:81-84
function NotAdoptable(descriptor: Desc, reason: seq<char>): Outcome
{
  Rpc(AlreadyExists,
    "Type '" + descriptor.TypeName + "' is not registered, but its table '" + descriptor.TableName + "' already " +
    "exists and " + reason + ", so the registration cannot take it over. Register the type under another name.")
}

// SchemaRegistrationApplier.cs:30  storedOwners.Any(owner => owner != descriptor.OwnerTenantId)
// (string? != string? is ordinal equality, null-aware: Option equality.)
function AnyOwnerDiffers(storedOwners: seq<Option<CsString>>, owner: Option<CsString>): bool
  decreases |storedOwners|
{
  if |storedOwners| == 0 then false
  else storedOwners[0] != owner || AnyOwnerDiffers(storedOwners[1..], owner)
}

// SchemaRegistrationApplier.cs:66-79. None = returned normally; Some(o) = threw o.
function RequireAdoptableAsync(descriptor: Desc, db: Db): Option<Outcome>
{
  var existing := db.describe(descriptor.TableName);
  if existing.None? then None
  else if !existing.value.IsOrdinaryTable then Some(NotAdoptable(descriptor, "is not an ordinary table"))
  else if !existing.value.HasTenantColumn then Some(NotAdoptable(descriptor, "has no " + descriptor.TenantColumn + " column"))
  else if descriptor.OwnerTenantId.Some? &&
          db.foreignRows(descriptor.TableName, descriptor.OwnerTenantId.value)
    then Some(NotAdoptable(descriptor, "holds rows of another tenant"))
  else None
}

// SchemaRegistrationApplier.cs:27-53, the body of the first foreach for one descriptor.
// Done = the iteration completed; any other outcome = the exception it threw.
function ApplyDescriptor(descriptor: Desc, db: Db): (Outcome, seq<Effect>)
{
  // 1. (lines 29-31)
  var storedOwners := db.owners(descriptor.TypeName);
  if AnyOwnerDiffers(storedOwners, descriptor.OwnerTenantId) then (OwnedByAnotherTenant(descriptor.TypeName), [])
  else
    // 2. (lines 37-38)
    var adopt := if !db.exists_(descriptor.TypeName) then RequireAdoptableAsync(descriptor, db) else None;
    if adopt.Some? then (adopt.value, [])
    else
      // 3. (lines 41-48)
      match db.ddl(descriptor.TableName)
      case Drift(m) => (Rpc(FailedPrecondition, m), [])
      case DdlFault => (Fault, [])
      case Applied =>
        // 4. (lines 51-53)
        var json := SerializeDescriptor(descriptor);
        if !db.upsert(descriptor.TypeName, descriptor.OwnerTenantId)
          then (OwnedByAnotherTenant(descriptor.TypeName), [Ddl(descriptor.TableName)])
        else (Done, [Ddl(descriptor.TableName), Write(descriptor.TypeName, descriptor.OwnerTenantId)])
}

// SchemaRegistrationApplier.cs:25-54, the first foreach; stops at the first failure.
function ExecuteDescriptors(descriptors: seq<Desc>, db: Db): (Outcome, seq<Effect>)
  decreases |descriptors|
{
  if |descriptors| == 0 then (Done, [])
  else
    var h := ApplyDescriptor(descriptors[0], db);
    if h.0 != Done then h
    else
      var t := ExecuteDescriptors(descriptors[1..], db);
      (t.0, h.1 + t.1)
}

// SchemaRegistrationApplier.cs:59-60
function EnqueueAll(changedTemplateTypes: seq<CsString>): seq<Effect>
  decreases |changedTemplateTypes|
{
  if |changedTemplateTypes| == 0 then []
  else [Enqueue(changedTemplateTypes[0])] + EnqueueAll(changedTemplateTypes[1..])
}

// SchemaRegistrationApplier.cs:20-61
function ExecuteAsync(ds: seq<Desc>, changed: seq<CsString>, db: Db): (Outcome, seq<Effect>)
{
  var r := ExecuteDescriptors(ds, db);
  if r.0 != Done then r
  else (Done, r.1 + EnqueueAll(changed))
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma AnyOwnerDiffersIff(s: seq<Option<CsString>>, x: Option<CsString>)
  ensures AnyOwnerDiffers(s, x) <==> exists o :: o in s && o != x
  decreases |s|
{
  if |s| > 0 {
    AnyOwnerDiffersIff(s[1..], x);
    if s[0] != x {
      assert s[0] in s;
    }
    if AnyOwnerDiffers(s[1..], x) {
      var o :| o in s[1..] && o != x;
      assert o in s;
    }
    if exists o :: o in s && o != x {
      var o :| o in s && o != x;
      if o != s[0] {
        assert o in s[1..] && o != x;
        assert AnyOwnerDiffers(s[1..], x);
      }
      assert AnyOwnerDiffers(s, x);
    }
  }
}

lemma SingleDescriptor(d: Desc, db: Db)
  ensures ExecuteDescriptors([d], db) == ApplyDescriptor(d, db)
{
  var h := ApplyDescriptor(d, db);
  if h.0 == Done {
    assert [d][1..] == [];
    assert ExecuteDescriptors([d][1..], db) == (Done, []);
    assert h.1 + [] == h.1;
  }
}

lemma EnqueueAllShape(changed: seq<CsString>)
  ensures |EnqueueAll(changed)| == |changed|
  ensures forall j :: 0 <= j < |changed| ==> EnqueueAll(changed)[j] == Enqueue(changed[j])
  decreases |changed|
{
  if |changed| > 0 {
    EnqueueAllShape(changed[1..]);
  }
}

lemma DescriptorsShape(ds: seq<Desc>, db: Db)
  requires ExecuteDescriptors(ds, db).0 == Done
  ensures var E := ExecuteDescriptors(ds, db).1;
    |E| == 2 * |ds| &&
    (forall i :: 0 <= i < |ds| ==> E[2 * i] == Ddl(ds[i].TableName) && E[2 * i + 1] == Write(ds[i].TypeName, ds[i].OwnerTenantId))
  decreases |ds|
{
  if |ds| > 0 {
    var h := ApplyDescriptor(ds[0], db);
    assert h.0 == Done;
    assert h.1 == [Ddl(ds[0].TableName), Write(ds[0].TypeName, ds[0].OwnerTenantId)];
    DescriptorsShape(ds[1..], db);
    var t := ExecuteDescriptors(ds[1..], db);
    var E := ExecuteDescriptors(ds, db).1;
    assert E == h.1 + t.1;
    forall i | 0 <= i < |ds|
      ensures E[2 * i] == Ddl(ds[i].TableName) && E[2 * i + 1] == Write(ds[i].TypeName, ds[i].OwnerTenantId)
    {
      if i > 0 {
        assert ds[i] == ds[1..][i - 1];
        assert E[2 * i] == t.1[2 * (i - 1)];
        assert E[2 * i + 1] == t.1[2 * (i - 1) + 1];
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "A different owner fails with `PermissionDenied`, using today's message." (spec §5.3, step 1)
lemma P1_AStoredRowOfAnotherOwnerIsRefusedBeforeAnything(d: Desc, db: Db)
  requires exists o :: o in db.owners(d.TypeName) && o != d.OwnerTenantId
  ensures ExecuteAsync([d], [], db).0 ==
    Rpc(PermissionDenied, "Type '" + d.TypeName + "' is registered to another tenant and cannot be re-registered here.")
  ensures ExecuteAsync([d], [], db).1 == []
{
  AnyOwnerDiffersIff(db.owners(d.TypeName), d.OwnerTenantId);
  SingleDescriptor(d, db);
}

// Citation: "The worker refuses with `AlreadyExists` unless:" / "the relation is an ordinary table (`relkind = 'r'`) with a `__TenantId` column; and" / "for an owned type, no row in it has a `__TenantId` other than the owner's (one `EXISTS` query under `iverson_maintenance`)." (spec §5.3)
lemma P2_AnUnregisteredTypeNeverAdoptsAForeignTable(d: Desc, db: Db)
  requires forall o :: o in db.owners(d.TypeName) ==> o == d.OwnerTenantId
  requires !db.exists_(d.TypeName) && db.describe(d.TableName).Some?
  requires var e := db.describe(d.TableName).value;
    !e.IsOrdinaryTable || !e.HasTenantColumn ||
    (d.OwnerTenantId.Some? && db.foreignRows(d.TableName, d.OwnerTenantId.value))
  ensures ExecuteAsync([d], [], db).0.Rpc? && ExecuteAsync([d], [], db).0.code == AlreadyExists
  ensures ExecuteAsync([d], [], db).1 == []
{
  AnyOwnerDiffersIff(db.owners(d.TypeName), d.OwnerTenantId);
  SingleDescriptor(d, db);
}

// Citation: "The documented model-change recovery (delete the registry row, then re-register) keeps working for types whose tables hold only the owner's rows." (spec §5.3)
lemma P3_TheModelChangeRecoveryAdoptsTheOwnersOwnTable(d: Desc, db: Db)
  requires forall o :: o in db.owners(d.TypeName) ==> o == d.OwnerTenantId
  requires !db.exists_(d.TypeName) && db.describe(d.TableName).Some?
  requires db.describe(d.TableName).value.IsOrdinaryTable && db.describe(d.TableName).value.HasTenantColumn
  requires d.OwnerTenantId.Some? ==> !db.foreignRows(d.TableName, d.OwnerTenantId.value)
  requires db.ddl(d.TableName) == Applied && db.upsert(d.TypeName, d.OwnerTenantId)
  ensures ExecuteAsync([d], [], db) == (Done, [Ddl(d.TableName), Write(d.TypeName, d.OwnerTenantId)])
{
  AnyOwnerDiffersIff(db.owners(d.TypeName), d.OwnerTenantId);
  SingleDescriptor(d, db);
  assert EnqueueAll([]) == [];
  assert [Ddl(d.TableName), Write(d.TypeName, d.OwnerTenantId)] + [] == [Ddl(d.TableName), Write(d.TypeName, d.OwnerTenantId)];
}

// Citation: "**DDL.** `ApplySchemaAsync` with `SchemaDriftPolicy.Throw`. Drift fails with `FailedPrecondition`, as today." (spec §5.3)
lemma P4_DriftIsFailedPreconditionAndNothingIsWritten(d: Desc, db: Db, m: seq<char>)
  requires forall o :: o in db.owners(d.TypeName) ==> o == d.OwnerTenantId
  requires db.exists_(d.TypeName) || db.describe(d.TableName).None?
  requires db.ddl(d.TableName) == Drift(m)
  ensures ExecuteAsync([d], [], db) == (Rpc(FailedPrecondition, m), [])
{
  AnyOwnerDiffersIff(db.owners(d.TypeName), d.OwnerTenantId);
  SingleDescriptor(d, db);
}

// Citation: "**Registry write.** `INSERT … ON CONFLICT (type_name) DO UPDATE … WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner`. Zero rows fails with `PermissionDenied`." (spec §5.3)
lemma P5_AConditionalWriteThatMatchesNoRowIsPermissionDenied(d: Desc, db: Db)
  requires forall o :: o in db.owners(d.TypeName) ==> o == d.OwnerTenantId
  requires db.exists_(d.TypeName) || db.describe(d.TableName).None?
  requires db.ddl(d.TableName) == Applied && !db.upsert(d.TypeName, d.OwnerTenantId)
  ensures ExecuteAsync([d], [], db).0 ==
    Rpc(PermissionDenied, "Type '" + d.TypeName + "' is registered to another tenant and cannot be re-registered here.")
{
  AnyOwnerDiffersIff(db.owners(d.TypeName), d.OwnerTenantId);
  SingleDescriptor(d, db);
}

// Citation: "### 5.3 The worker's RegisterSchema, per descriptor in batch order" / "**Re-renders.** It queues re-renders for changed templates." (spec §5.3) and the `Received.InOrder` of plan T7 S1b `ChangedTemplates_AreEnqueuedAfterEveryDescriptorIsWritten`
lemma P6_EachDescriptorIsAppliedThenWrittenInOrder_ThenTheReRenders(ds: seq<Desc>, changed: seq<CsString>, db: Db)
  requires ExecuteAsync(ds, changed, db).0 == Done
  ensures var E := ExecuteAsync(ds, changed, db).1;
    |E| == 2 * |ds| + |changed| &&
    (forall i :: 0 <= i < |ds| ==> E[2 * i] == Ddl(ds[i].TableName) && E[2 * i + 1] == Write(ds[i].TypeName, ds[i].OwnerTenantId)) &&
    (forall j :: 0 <= j < |changed| ==> E[2 * |ds| + j] == Enqueue(changed[j]))
{
  var r := ExecuteDescriptors(ds, db);
  assert r.0 == Done;
  DescriptorsShape(ds, db);
  EnqueueAllShape(changed);
  var E := ExecuteAsync(ds, changed, db).1;
  assert E == r.1 + EnqueueAll(changed);
}
