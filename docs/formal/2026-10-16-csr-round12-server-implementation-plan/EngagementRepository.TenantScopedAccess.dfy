// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T10 S3
// Source: Iverson.Server/Iverson.StarRocks/EngagementRepository.cs `TenantConnectionStringFor`, `IsRecordedAsync`, `RunTenantScopedAsync`, `StreamTenantScopedAsync`, and the tenant-scoped paths of `SearchAsync`, `AggregateAsync`, `GroupByAsync`, `PipelineAsync`, `MatchRowsAsync`, `UpsertAsync`, `DeleteAsync`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/EngagementRepository.TenantScopedAccess.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary (only what this candidate uses).
// ---------------------------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

// ---------------------------------------------------------------------------------------------
// Approved axioms.
// ---------------------------------------------------------------------------------------------

// internal static string TenantConnectionString(string baseConnectionString, string tenantId, string tenantLoginKey)
// (MySqlConnectionStringBuilder with the tenant's LoginName / LoginPassword, no database, the pool bounds).
function TenantConnectionString(baseCs: CsString, tenantId: CsString, key: CsString): CsString

// TenantIdentifier.IsValid(tenantId)  (AllowedPattern.IsMatch)
predicate IsValidTenant(tenantId: CsString)

// ---------------------------------------------------------------------------------------------
// Model vocabulary for this candidate.
// ---------------------------------------------------------------------------------------------

datatype Op = Search | Aggregate | GroupBy | Pipeline | MatchRows | Upsert | Delete

// The instance state the paths read: the base login `connectionString`, `tenantLoginKey` (string?), and
// whether `provisionedTenants` (IProvisionedTenantDirectory?) is non-null.
datatype Repo = Repo(BaseCs: CsString, TenantLoginKey: Option<CsString>, HasDirectory: bool)

// InvalidOperation: InvalidOperationException; NotReady: EngagementNotReadyException;
// MySql(code): a MySqlException with that ErrorCode; OtherEx: anything else (ArgumentException,
// EngagementQueryTranslationException, a builder's exception, a JsonException, ...).
datatype Exn = InvalidOperation | NotReady | MySql(code: int) | OtherEx

// Empty: the reader's own empty value ([], null, an empty stream) or a void method's plain return.
datatype Answer = Empty | Rows | Fault(e: Exn)

datatype DataEffect = OpenedData(connectionString: CsString) | RanOwnQuery | RanOther(sql: seq<char>)

// A step's outcome. A thrown exception carries whether its Message contains "Unknown table"
// (the only part of the message IsExpectedMissingResourceError reads).
datatype Outcome<T> = Ret(value: T) | Raise(ex: Exn, unknownTable: bool)

// MySqlErrorCode.AccessDenied.
const AccessDeniedCode: int := 1045

// The data store's behaviour on the one run of the operation that RunAsync makes (outside the candidate;
// the readiness gate, breaker, retry and liveness probe are abstracted to these cases):
//   BeforeRunNotReady:             the readiness gate or an open circuit answers EngagementNotReadyException
//                                  without running the operation;
//   OpenFails(e, ut, rewrap):      conn.OpenAsync throws e;
//   QueryFails(e, ut, rewrap):     the open succeeds and the statement (Dapper / ExecuteReaderAsync) throws e;
//   QueryOk:                       the open and the statement succeed.
// `rewrap` is "RewrapIfBackendUnavailableAsync would find the backend not alive (and e not transient)".
datatype DbRun =
  | BeforeRunNotReady
  | OpenFails(openEx: Exn, openUnknownTable: bool, openRewrap: bool)
  | QueryFails(queryEx: Exn, queryUnknownTable: bool, queryRewrap: bool)
  | QueryOk

// ---------------------------------------------------------------------------------------------
// Transliteration.
// ---------------------------------------------------------------------------------------------

// private static bool IsExpectedMissingResourceError(Exception ex) =>
//     ex is MySqlException mex && mex.ErrorCode == (MySqlErrorCode)5502 &&
//     mex.Message.Contains("Unknown table", StringComparison.OrdinalIgnoreCase);
predicate IsExpectedMissingResourceError(e: Exn, unknownTable: bool)
{
  e.MySql? && e.code == 5502 && unknownTable
}

// private string TenantConnectionStringFor(string tenantId) =>
//     TenantConnectionString(connectionString, tenantId,
//         tenantLoginKey ?? throw new InvalidOperationException(NoTenantLoginsMessage));
function TenantConnectionStringFor(repo: Repo, tenantId: CsString): Outcome<CsString>
{
  if repo.TenantLoginKey.None? then Raise(InvalidOperation, false)
  else Ret(TenantConnectionString(repo.BaseCs, tenantId, repo.TenantLoginKey.value))
}

// private Task<bool> IsRecordedAsync(string tenantId) =>
//     (provisionedTenants ?? throw new InvalidOperationException(NoTenantLoginsMessage))
//         .IsProvisionedAsync(tenantId);
// `recorded` is the directory's answer.
function IsRecorded(repo: Repo, tenantId: CsString, recorded: bool): Outcome<bool>
{
  if !repo.HasDirectory then Raise(InvalidOperation, false)
  else Ret(recorded)
}

// internal static Task OpenTenantConnectionAsync(Func<Task> open) =>
//     OpenOrSurfaceRefusalAsync(open, "StarRocks refused the tenant's login.");
// (catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied) => EngagementNotReadyException)
function OpenTenantConnection(e: Exn, unknownTable: bool): Outcome<()>
{
  if e.MySql? && e.code == AccessDeniedCode then Raise(NotReady, false)
  else Raise(e, unknownTable)
}

// RunAsync's own catch (MySqlException ex): RewrapIfBackendUnavailableAsync returns non-null only for a
// MySqlException that is neither transient nor IsExpectedMissingResourceError, and only when the backend is
// not shown alive; `rewrap` abstracts "not transient and not alive". Anything else rethrows unchanged.
function RunAsyncRethrow(e: Exn, unknownTable: bool, rewrap: bool): Outcome<()>
{
  if e.MySql? && !IsExpectedMissingResourceError(e, unknownTable) && rewrap then Raise(NotReady, false)
  else Raise(e, unknownTable)
}

// The body RunAsync runs once, on `tenantConnectionString` (both RunTenantScopedAsync and
// StreamTenantScopedAsync):
//   await using var conn = new MySqlConnection(tenantConnectionString);   (Stream: var c = new ...)
//   await OpenTenantConnectionAsync(() => conn.OpenAsync());
//   return await operation(conn);                                        (Stream: c.ExecuteReaderAsync(...))
// with RunAsync's gate before it and its catch after it.
function RunTenantBody(cs: CsString, db: DbRun): (Outcome<()>, seq<DataEffect>)
{
  match db
  case BeforeRunNotReady => (Raise(NotReady, false), [])
  case OpenFails(e, ut, rw) =>
    (RunAsyncRethrow(OpenTenantConnection(e, ut).ex, OpenTenantConnection(e, ut).unknownTable, rw), [OpenedData(cs)])
  case QueryFails(e, ut, rw) =>
    (RunAsyncRethrow(e, ut, rw), [OpenedData(cs), RanOwnQuery])
  case QueryOk =>
    (Ret(()), [OpenedData(cs), RanOwnQuery])
}

// private async Task<T> RunTenantScopedAsync<T>(string activityName, string tenantId, string sql,
//     Func<MySqlConnection, Task<T>> operation, Func<Exception, bool>? isExpectedException = null)
// The activity / telemetry branches change only the activity's status: every exception is rethrown.
function RunTenantScoped(repo: Repo, tenantId: CsString, db: DbRun): (Outcome<()>, seq<DataEffect>)
{
  // if (!TenantIdentifier.IsValid(tenantId)) throw new ArgumentException(...)
  if !IsValidTenant(tenantId) then (Raise(OtherEx, false), [])
  else
    // var tenantConnectionString = TenantConnectionStringFor(tenantId);
    var csr := TenantConnectionStringFor(repo, tenantId);
    if csr.Raise? then (Raise(csr.ex, csr.unknownTable), [])
    else
      // var result = await RunAsync(async () => { ... }); ... catch (Exception ex) { ...; throw; }
      RunTenantBody(csr.value, db)
}

// internal async IAsyncEnumerable<IDictionary<string, object?>> StreamTenantScopedAsync(
//     string activityName, string tenantId, string sql, object param,
//     Func<Exception, bool> isExpectedMissingResource, [EnumeratorCancellation] CancellationToken ct)
// Enumerated to its end. A failure inside the RunAsync body releases the connection (ReleaseAsync) and
// rethrows; a failure reading rows after the reader started is abstracted (it does not change the effects).
function StreamTenantScoped(repo: Repo, tenantId: CsString, db: DbRun): (Answer, seq<DataEffect>)
{
  // if (!TenantIdentifier.IsValid(tenantId)) throw new ArgumentException(...)
  if !IsValidTenant(tenantId) then (Fault(OtherEx), [])
  else
    // var tenantConnectionString = TenantConnectionStringFor(tenantId);
    var csr := TenantConnectionStringFor(repo, tenantId);
    if csr.Raise? then (Fault(csr.ex), [])
    else
      var (o, fx) := RunTenantBody(csr.value, db);
      if o.Raise? then
        // catch (Exception ex) when (isExpectedMissingResource(ex)) { ... }  => reader is null => yield break
        if IsExpectedMissingResourceError(o.ex, o.unknownTable) then (Empty, fx)
        // catch (Exception ex) { MarkFailed(activity, ex); throw; }
        else (Fault(o.ex), fx)
      else
        // while (await ReadRowAsync(reader, activity, ct) is { } row) yield return row;
        (Rows, fx)
}

// The tenant-scoped (authz != null) part of SearchAsync, GroupByAsync and PipelineAsync, and of
// AggregateAsync's else-branch, for tenant value `tenantId`:
//   if (tenantId is null || !TenantIdentifier.IsValid(tenantId)) return [];      (Aggregate: null)
//   var (sql, param[, lastCols]) = <Builder>(...);                                 (sqlBuilt)
//   if (!await IsRecordedAsync(tenantId)) return [];                               (Aggregate: null)
//   try { return await RunTenantScopedAsync("sr.query", tenantId, sql, ..., IsExpectedMissingResourceError); }
//   catch (Exception ex) when (IsExpectedMissingResourceError(ex)) { return []; }  (Aggregate: null)
function ScopedRead(repo: Repo, tenantId: CsString, recorded: bool, sqlBuilt: bool, db: DbRun): (Answer, seq<DataEffect>)
{
  if !IsValidTenant(tenantId) then (Empty, [])
  else if !sqlBuilt then (Fault(OtherEx), [])
  else
    var rec := IsRecorded(repo, tenantId, recorded);
    if rec.Raise? then (Fault(rec.ex), [])
    else if !rec.value then (Empty, [])
    else
      var (o, fx) := RunTenantScoped(repo, tenantId, db);
      if o.Raise? then
        if IsExpectedMissingResourceError(o.ex, o.unknownTable) then (Empty, fx)
        else (Fault(o.ex), fx)
      else
        // Pipeline: MaskPipelineRows(rows, lastCols); Aggregate: the bucket / metric decoding below the
        // else-branch. Neither connects; their own failures are abstracted.
        (Rows, fx)
}

// public async Task<AggregationResult?> AggregateAsync(...)
//   if (spec.GroupByFields is { Count: > 1 }) throw new EngagementQueryTranslationException(...);
//   ... else { <ScopedRead> }
function AggregateScoped(repo: Repo, tenantId: CsString, recorded: bool, singleKey: bool, sqlBuilt: bool, db: DbRun): (Answer, seq<DataEffect>)
{
  if !singleKey then (Fault(OtherEx), [])
  else ScopedRead(repo, tenantId, recorded, sqlBuilt, db)
}

// public async IAsyncEnumerable<IDictionary<string, object?>> MatchRowsAsync(...)
//   var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
//   if (tenantId is null || !TenantIdentifier.IsValid(tenantId)) yield break;
//   var (sql, param) = MatchRowsQueryBuilder.Build(...);                           (sqlBuilt)
//   if (!await IsRecordedAsync(tenantId)) yield break;
//   await foreach (var row in StreamTenantScopedAsync("sr.match_rows", tenantId, sql, param,
//                                                     IsExpectedMissingResourceError, ct)) yield return row;
function MatchRowsScoped(repo: Repo, tenantId: CsString, recorded: bool, sqlBuilt: bool, db: DbRun): (Answer, seq<DataEffect>)
{
  if !IsValidTenant(tenantId) then (Empty, [])
  else if !sqlBuilt then (Fault(OtherEx), [])
  else
    var rec := IsRecorded(repo, tenantId, recorded);
    if rec.Raise? then (Fault(rec.ex), [])
    else if !rec.value then (Empty, [])
    else StreamTenantScoped(repo, tenantId, db)
}

// public async Task UpsertAsync(EngagementTableSchema schema, string payloadJson, string tenantId)
//   if (!TenantIdentifier.IsValid(tenantId)) return;
//   var entries = UpsertEntries(schema, payloadJson);         (may throw: sqlBuilt == false)
//   if (entries.Count == 0) return;                           (hasEntries)
//   var sql = $"INSERT INTO {qualifiedTable} (...) VALUES (...)"; ...
//   StarRocksParameterGuard.EnsureAllPlaceholdersBound(sql, param);   (may throw: sqlBuilt == false)
//   await RunTenantScopedAsync("sr.execute", tenantId, sql, conn => conn.ExecuteAsync(sql, param));
function UpsertScoped(repo: Repo, tenantId: CsString, sqlBuilt: bool, hasEntries: bool, db: DbRun): (Answer, seq<DataEffect>)
{
  if !IsValidTenant(tenantId) then (Empty, [])
  else if !sqlBuilt then (Fault(OtherEx), [])
  else if !hasEntries then (Empty, [])
  else
    var (o, fx) := RunTenantScoped(repo, tenantId, db);
    if o.Raise? then (Fault(o.ex), fx) else (Empty, fx)
}

// public async Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId)
//   if (!TenantIdentifier.IsValid(tenantId)) return;
//   if (!await IsRecordedAsync(tenantId)) return;
//   var sql = $"DELETE FROM {qualifiedTable} WHERE `{keyColumn}` = @key";
//   StarRocksParameterGuard.EnsureAllPlaceholdersBound(sql, new { key = keyValue });   (sqlBuilt)
//   await RunTenantScopedAsync("sr.execute", tenantId, sql, conn => conn.ExecuteAsync(sql, new { key = keyValue }));
function DeleteScoped(repo: Repo, tenantId: CsString, recorded: bool, sqlBuilt: bool, db: DbRun): (Answer, seq<DataEffect>)
{
  if !IsValidTenant(tenantId) then (Empty, [])
  else
    var rec := IsRecorded(repo, tenantId, recorded);
    if rec.Raise? then (Fault(rec.ex), [])
    else if !rec.value then (Empty, [])
    else if !sqlBuilt then (Fault(OtherEx), [])
    else
      var (o, fx) := RunTenantScoped(repo, tenantId, db);
      if o.Raise? then (Fault(o.ex), fx) else (Empty, fx)
}

// Every tenant-scoped path, with every input the frozen TenantOp leaves out.
function TenantOpFull(op: Op, repo: Repo, tenantId: CsString, recorded: bool, sqlBuilt: bool,
                      aggregateSingleKey: bool, upsertHasEntries: bool, db: DbRun): (Answer, seq<DataEffect>)
{
  match op
  case Search    => ScopedRead(repo, tenantId, recorded, sqlBuilt, db)
  case Aggregate => AggregateScoped(repo, tenantId, recorded, aggregateSingleKey, sqlBuilt, db)
  case GroupBy   => ScopedRead(repo, tenantId, recorded, sqlBuilt, db)
  case Pipeline  => ScopedRead(repo, tenantId, recorded, sqlBuilt, db)
  case MatchRows => MatchRowsScoped(repo, tenantId, recorded, sqlBuilt, db)
  case Upsert    => UpsertScoped(repo, tenantId, sqlBuilt, upsertHasEntries, db)
  case Delete    => DeleteScoped(repo, tenantId, recorded, sqlBuilt, db)
}

// The frozen headers' TenantOp: a translatable (single-key) Aggregate, an Upsert whose payload names at
// least one column, and a data store whose one run succeeds.
function TenantOp(op: Op, repo: Repo, tenantId: CsString, recorded: bool, sqlBuilt: bool): (Answer, seq<DataEffect>)
{
  TenantOpFull(op, repo, tenantId, recorded, sqlBuilt, true, true, QueryOk)
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas (each proved).
// ---------------------------------------------------------------------------------------------

predicate OnlyOwnLogin(fx: seq<DataEffect>, cs: CsString)
{
  forall e :: e in fx ==> !e.RanOther? && (e.OpenedData? ==> e.connectionString == cs)
}

lemma RunTenantBodyOwnLogin(cs: CsString, db: DbRun)
  ensures OnlyOwnLogin(RunTenantBody(cs, db).1, cs)
{
}

lemma RunTenantScopedOwnLogin(repo: Repo, tenantId: CsString, db: DbRun)
  requires repo.TenantLoginKey.Some?
  ensures OnlyOwnLogin(RunTenantScoped(repo, tenantId, db).1,
                       TenantConnectionString(repo.BaseCs, tenantId, repo.TenantLoginKey.value))
{
  if IsValidTenant(tenantId) {
    RunTenantBodyOwnLogin(TenantConnectionString(repo.BaseCs, tenantId, repo.TenantLoginKey.value), db);
  }
}

lemma StreamTenantScopedOwnLogin(repo: Repo, tenantId: CsString, db: DbRun)
  requires repo.TenantLoginKey.Some?
  ensures OnlyOwnLogin(StreamTenantScoped(repo, tenantId, db).1,
                       TenantConnectionString(repo.BaseCs, tenantId, repo.TenantLoginKey.value))
{
  if IsValidTenant(tenantId) {
    RunTenantBodyOwnLogin(TenantConnectionString(repo.BaseCs, tenantId, repo.TenantLoginKey.value), db);
  }
}

// P4 over every input TenantOp fixes: any aggregate shape, any upsert payload, any data-store behaviour.
lemma TenantOpFullOwnLogin(op: Op, repo: Repo, tenantId: CsString, recorded: bool, sqlBuilt: bool,
                           aggregateSingleKey: bool, upsertHasEntries: bool, db: DbRun)
  requires repo.TenantLoginKey.Some?
  ensures OnlyOwnLogin(TenantOpFull(op, repo, tenantId, recorded, sqlBuilt, aggregateSingleKey, upsertHasEntries, db).1,
                       TenantConnectionString(repo.BaseCs, tenantId, repo.TenantLoginKey.value))
{
  RunTenantScopedOwnLogin(repo, tenantId, db);
  StreamTenantScopedOwnLogin(repo, tenantId, db);
}

// The inputs TenantOp leaves out never connect before the directory and key checks: a multi-key Aggregate
// faults for every repository without connecting, and an Upsert with no known column returns without
// connecting.
lemma ExcludedInputsDoNotConnect(repo: Repo, tenantId: CsString, recorded: bool, sqlBuilt: bool, db: DbRun)
  ensures TenantOpFull(Aggregate, repo, tenantId, recorded, sqlBuilt, false, true, db) == (Fault(OtherEx), [])
  ensures sqlBuilt ==> TenantOpFull(Upsert, repo, tenantId, recorded, sqlBuilt, true, false, db) == (Empty, [])
{
}

// P1-P3 hold for every data-store behaviour, not only QueryOk.
lemma P1toP3ForEveryDbRun(op: Op, repo: Repo, tenantId: CsString, recorded: bool, db: DbRun)
  requires IsValidTenant(tenantId)
  ensures op != Upsert && repo.HasDirectory ==>
    TenantOpFull(op, repo, tenantId, false, true, true, true, db) == (Empty, [])
  ensures op != Upsert && !repo.HasDirectory ==>
    TenantOpFull(op, repo, tenantId, recorded, true, true, true, db) == (Fault(InvalidOperation), [])
  ensures repo.TenantLoginKey.None? && (op == Upsert || repo.HasDirectory) ==>
    TenantOpFull(op, repo, tenantId, true, true, true, true, db) == (Fault(InvalidOperation), [])
{
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen).
// ---------------------------------------------------------------------------------------------

// Citation: "An unrecorded tenant reads as empty: `[]`, `null` or an empty stream, as today." / "A Delete for an unrecorded tenant does nothing: there is no StarRocks data to delete." (spec §7.3)
lemma P1_AnUnrecordedTenantReadsAsEmptyWithoutConnecting(op: Op, repo: Repo, tenantId: CsString)
  requires IsValidTenant(tenantId)
  requires op != Upsert && repo.HasDirectory
  ensures TenantOp(op, repo, tenantId, false, true) == (Empty, [])
{
}

// Citation: "Every StarRocks reader consults it before connecting: the api's read paths and the worker's popularity aggregates." (spec §7.3) and "await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");" (plan T10 S1c, `NoDirectory_RefusesReads_BeforeConnecting`)
lemma P2_WithoutTheDirectoryTenantAccessRefusesBeforeConnecting(op: Op, repo: Repo, tenantId: CsString, recorded: bool)
  requires IsValidTenant(tenantId)
  requires op != Upsert && !repo.HasDirectory
  ensures TenantOp(op, repo, tenantId, recorded, true) == (Fault(InvalidOperation), [])
{
}

// Citation: "await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant login key*");" / "await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant login key*");" (plan T10 S1c, `NoTenantLoginKey_RefusesTenantScopedReadsAndWrites_BeforeConnecting`)
lemma P3_WithoutTheKeyTenantAccessRefusesBeforeConnecting(op: Op, repo: Repo, tenantId: CsString)
  requires IsValidTenant(tenantId)
  requires repo.TenantLoginKey.None? && (op == Upsert || repo.HasDirectory)
  ensures TenantOp(op, repo, tenantId, true, true) == (Fault(InvalidOperation), [])
{
}

// Citation: "every StarRocks data connection carries exactly one tenant's role;" (spec §1) and "`SET ROLE` and `SET ROLE NONE` are removed from `RunTenantScopedAsync` and `StreamTenantScopedAsync`. Nothing may change a data connection's role or session state." (spec §7.2)
lemma P4_DataRunsOnlyOnTheTenantsOwnLoginWithNoRoleChange(op: Op, repo: Repo, tenantId: CsString, recorded: bool, sqlBuilt: bool)
  requires IsValidTenant(tenantId)
  requires repo.TenantLoginKey.Some?
  ensures forall e :: e in TenantOp(op, repo, tenantId, recorded, sqlBuilt).1 ==>
    !e.RanOther? &&
    (e.OpenedData? ==> e.connectionString == TenantConnectionString(repo.BaseCs, tenantId, repo.TenantLoginKey.value))
{
  TenantOpFullOwnLogin(op, repo, tenantId, recorded, sqlBuilt, true, true, QueryOk);
}
