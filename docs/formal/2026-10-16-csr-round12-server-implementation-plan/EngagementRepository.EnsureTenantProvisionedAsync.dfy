// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T10 S3
// Source: Iverson.Server/Iverson.StarRocks/EngagementRepository.cs `EnsureTenantProvisionedAsync` (as T10 leaves it)
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/EngagementRepository.EnsureTenantProvisionedAsync.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype Result<T> = Ok(value: T) | Threw

// As in the TenantScopedAccess candidate.
datatype Exn = InvalidOperation | NotReady | MySql(code: int) | OtherEx

// ---------------------------------------------------------------------------------------------
// Candidate model
// ---------------------------------------------------------------------------------------------

// The constructor arguments EnsureTenantProvisionedAsync reads: provisionerConnectionString (nullable),
// tenantLoginKey (nullable) and provisionedTenants (nullable; only its presence matters here).
datatype ProvRepo = ProvRepo(ProvisionerCs: Option<CsString>, TenantLoginKey: Option<CsString>, HasDirectory: bool)

datatype ProvEffect = OpenedProvisioner | Ran(sql: seq<char>) | CreatedTable | Widened | Recorded(tenantId: CsString)

datatype ProvOutcome = Provisioned | ProvFault(e: Exn)

// EngagementTableSchema: only TableName is read by this member outside the opaque BuildCreateTableDdl and
// WidenLargeTextColumnsAsync.
datatype EngTable = EngTable(TableName: CsString)

// ---------------------------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------------------------

function RoleName(tenantId: CsString): CsString

function LoginName(tenantId: CsString): CsString

function LoginPassword(tenantId: CsString, key: CsString): CsString

function CreateTableDdl(table: EngTable, qualifiedTable: CsString): CsString

function DatabaseName(tenantId: CsString): CsString

predicate IsValidTenant(tenantId: CsString)

// ---------------------------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------------------------

// TenantIdentifier.cs:138-139 (pre-plan).
function Qualify(tenantDatabase: Option<CsString>, tableName: CsString): seq<char>
{
  if tenantDatabase.None? then "`" + tableName + "`"
  else "`" + tenantDatabase.value + "`.`" + tableName + "`"
}

// EngagementRepository.cs:53-54.
const TenantSessionProperties: seq<char> :=
  "'session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296'"

// Runs `stmts` in order; the statement whose provisioning index (first + its position) equals failAt throws,
// and only the statements before it took effect.
function RunStatements(stmts: seq<ProvEffect>, first: nat, failAt: Option<nat>): (bool, seq<ProvEffect>)
{
  if failAt.Some? && first <= failAt.value < first + |stmts| then (false, stmts[..failAt.value - first])
  else (true, stmts)
}

// EngagementRepository.cs:581-583: the open and the first SET ROLE, before the try (indices 0, 1).
function BeforeTry(): seq<ProvEffect>
{
  [ OpenedProvisioner,
    Ran("SET ROLE user_admin") ]
}

// EngagementRepository.cs:586-620: the try block (indices 2 .. 12).
function TryBlock(dbName: CsString, roleName: CsString, loginName: CsString, loginPassword: CsString): seq<ProvEffect>
{
  [ Ran("CREATE DATABASE IF NOT EXISTS `" + dbName + "`"),
    Ran("CREATE ROLE IF NOT EXISTS `" + roleName + "`"),
    Ran("GRANT SELECT, INSERT, UPDATE, DELETE ON `" + dbName + "`.* TO ROLE `" + roleName + "`"),
    Ran("GRANT CREATE TABLE ON DATABASE `" + dbName + "` TO ROLE `" + roleName + "`"),
    Ran("CREATE USER IF NOT EXISTS '" + loginName + "'@'%' IDENTIFIED BY '" + loginPassword + "' DEFAULT ROLE '" + roleName + "'"),
    Ran("ALTER USER '" + loginName + "'@'%' SET PROPERTIES (" + TenantSessionProperties + ")"),
    Ran("GRANT `" + roleName + "` TO USER 'iverson_provisioner'@'%'"),
    Ran("GRANT ALTER ON ALL TABLES IN DATABASE `" + dbName + "` TO USER 'iverson_provisioner'@'%'"),
    Ran("SET ROLE user_admin, `" + roleName + "`"),
    CreatedTable,
    Widened ]
}

// EngagementRepository.cs:622-632: the finally block; a throw from SET ROLE NONE is caught and logged.
const FinallyBlock: seq<ProvEffect> := [Ran("SET ROLE NONE")]

// EngagementRepository.cs:579-633: the lambda RunAsync runs, modelled as running once.
function ProvisionBody(dbName: CsString, roleName: CsString, loginName: CsString, loginPassword: CsString,
                       failAt: Option<nat>): (Result<bool>, seq<ProvEffect>)
{
  var open := RunStatements(BeforeTry(), 0, failAt);
  if !open.0 then (Threw, open.1)
  else
    var tried := RunStatements(TryBlock(dbName, roleName, loginName, loginPassword), |BeforeTry()|, failAt);
    var effects := open.1 + tried.1 + FinallyBlock;
    if !tried.0 then (Threw, effects)
    else (Ok(true), effects)
}

// EngagementRepository.cs:552-638. failAt indexes the statements 0 .. 12 above; RecordProvisionedAsync (636) is
// index 13, and every failAt past the body (13 or more) means the record throws.
function Provision(repo: ProvRepo, tenantId: CsString, table: EngTable, failAt: Option<nat>): (ProvOutcome, seq<ProvEffect>)
{
  if !IsValidTenant(tenantId) then (Provisioned, [])
  else if repo.ProvisionerCs.None? then (ProvFault(InvalidOperation), [])
  else if !repo.HasDirectory then (ProvFault(InvalidOperation), [])
  else if repo.TenantLoginKey.None? then (ProvFault(InvalidOperation), [])
  else
    var loginPassword := LoginPassword(tenantId, repo.TenantLoginKey.value);
    var loginName := LoginName(tenantId);
    var dbName := DatabaseName(tenantId);
    var roleName := RoleName(tenantId);
    var qualifiedTable := Qualify(Some(dbName), table.TableName);
    // Deviation: string concatenation is not length-bounded in this model (shared vocabulary); the DDL is
    // only computed for a qualified name that fits a CsString, and feeds the opaque CreatedTable either way.
    var createTableDdl := if |qualifiedTable| < 0x8000_0000 then Some(CreateTableDdl(table, qualifiedTable)) else None;
    var ran := ProvisionBody(dbName, roleName, loginName, loginPassword, failAt);
    if ran.0.Threw? then (ProvFault(OtherEx), ran.1)
    else if failAt.Some? then (ProvFault(OtherEx), ran.1)   // RecordProvisionedAsync threw (failAt >= 13)
    else (Provisioned, ran.1 + [Recorded(tenantId)])
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------------------------

lemma NoRecordedInBody(dbName: CsString, roleName: CsString, loginName: CsString, loginPassword: CsString,
                       failAt: Option<nat>, tenantId: CsString)
  ensures Recorded(tenantId) !in ProvisionBody(dbName, roleName, loginName, loginPassword, failAt).1
{
  var all := BeforeTry() + TryBlock(dbName, roleName, loginName, loginPassword) + FinallyBlock;
  forall e | e in all ensures !e.Recorded? {}
  var r := ProvisionBody(dbName, roleName, loginName, loginPassword, failAt);
  forall e | e in r.1 ensures e in all {}
}

// The ALTER USER tail as the code builds it equals the literal tail the property names. Z3 cannot unfold a long
// string literal on the right of `+` in one step, so TenantSessionProperties is appended a short chunk at a time.
lemma SessionPropertiesTail()
  ensures "'@'%' SET PROPERTIES (" + TenantSessionProperties + ")" ==
          "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296')"
{
  // H + TSP, built one short chunk of TSP at a time.
  assert "'@'%' SET PROPERTIES (" + "'sessi" == "'@'%' SET PROPERTIES ('sessi";
  assert "'session.que" == "'sessi" + "on.que";
  assert "'@'%' SET PROPERTIES (" + "'session.que" == ("'@'%' SET PROPERTIES (" + "'sessi") + "on.que";
  assert "'@'%' SET PROPERTIES ('sessi" + "on.que" == "'@'%' SET PROPERTIES ('session.que";
  assert "'session.query_tim" == "'session.que" + "ry_tim";
  assert "'@'%' SET PROPERTIES (" + "'session.query_tim" == ("'@'%' SET PROPERTIES (" + "'session.que") + "ry_tim";
  assert "'@'%' SET PROPERTIES ('session.que" + "ry_tim" == "'@'%' SET PROPERTIES ('session.query_tim";
  assert "'session.query_timeout' " == "'session.query_tim" + "eout' ";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' " == ("'@'%' SET PROPERTIES (" + "'session.query_tim") + "eout' ";
  assert "'@'%' SET PROPERTIES ('session.query_tim" + "eout' " == "'@'%' SET PROPERTIES ('session.query_timeout' ";
  assert "'session.query_timeout' = '180" == "'session.query_timeout' " + "= '180";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180" == ("'@'%' SET PROPERTIES (" + "'session.query_timeout' ") + "= '180";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' " + "= '180" == "'@'%' SET PROPERTIES ('session.query_timeout' = '180";
  assert "'session.query_timeout' = '180', 'se" == "'session.query_timeout' = '180" + "', 'se";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'se" == ("'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180") + "', 'se";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' = '180" + "', 'se" == "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'se";
  assert "'session.query_timeout' = '180', 'session." == "'session.query_timeout' = '180', 'se" + "ssion.";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session." == ("'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'se") + "ssion.";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'se" + "ssion." == "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.";
  assert "'session.query_timeout' = '180', 'session.exec_m" == "'session.query_timeout' = '180', 'session." + "exec_m";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_m" == ("'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.") + "exec_m";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session." + "exec_m" == "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_m";
  assert "'session.query_timeout' = '180', 'session.exec_mem_lim" == "'session.query_timeout' = '180', 'session.exec_m" + "em_lim";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_mem_lim" == ("'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_m") + "em_lim";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_m" + "em_lim" == "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_lim";
  assert "'session.query_timeout' = '180', 'session.exec_mem_limit' = " == "'session.query_timeout' = '180', 'session.exec_mem_lim" + "it' = ";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_mem_limit' = " == ("'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_mem_lim") + "it' = ";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_lim" + "it' = " == "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = ";
  assert "'session.query_timeout' = '180', 'session.exec_mem_limit' = '42949" == "'session.query_timeout' = '180', 'session.exec_mem_limit' = " + "'42949";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_mem_limit' = '42949" == ("'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_mem_limit' = ") + "'42949";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = " + "'42949" == "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '42949";
  assert "'session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296'" == "'session.query_timeout' = '180', 'session.exec_mem_limit' = '42949" + "67296'";
  assert "'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296'" == ("'@'%' SET PROPERTIES (" + "'session.query_timeout' = '180', 'session.exec_mem_limit' = '42949") + "67296'";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '42949" + "67296'" == "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296'";
  assert "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296'" + ")" == "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296')";
}

lemma DiffersAt(a: seq<char>, b: seq<char>, k: nat)
  requires k < |a| && k < |b| && a[k] != b[k]
  ensures a != b
{}

// ---------------------------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------------------------

// Citation: "New plumbing table `IversonStarRocksTenants (TenantId text PK, ProvisionedAt timestamptz)`. The worker inserts a row after each successful provisioning." (spec §7.3) and "(await directory.IsProvisionedAsync("t1")).Should().BeFalse();" (plan T10 S1c, `ProvisioningThatFails_DoesNotRecordTheTenant`)
lemma P1_ATenantIsRecordedOnlyAfterItsProvisioningSucceeds(repo: ProvRepo, tenantId: CsString, table: EngTable, failAt: Option<nat>)
  requires IsValidTenant(tenantId)
  ensures Recorded(tenantId) in Provision(repo, tenantId, table, failAt).1 ==>
    failAt.None? &&
    Provision(repo, tenantId, table, failAt).1[|Provision(repo, tenantId, table, failAt).1| - 1] == Recorded(tenantId)
  ensures repo.ProvisionerCs.Some? && repo.TenantLoginKey.Some? && repo.HasDirectory && failAt.None? ==>
    Provision(repo, tenantId, table, failAt).0 == Provisioned &&
    Recorded(tenantId) in Provision(repo, tenantId, table, failAt).1
{
  if repo.ProvisionerCs.Some? && repo.HasDirectory && repo.TenantLoginKey.Some? {
    var loginPassword := LoginPassword(tenantId, repo.TenantLoginKey.value);
    var loginName := LoginName(tenantId);
    var dbName := DatabaseName(tenantId);
    var roleName := RoleName(tenantId);
    NoRecordedInBody(dbName, roleName, loginName, loginPassword, failAt, tenantId);
    var body := ProvisionBody(dbName, roleName, loginName, loginPassword, failAt);
    if failAt.None? {
      assert body.0 == Ok(true);
      var r := Provision(repo, tenantId, table, failAt);
      assert r == (Provisioned, body.1 + [Recorded(tenantId)]);
      assert r.1[|r.1| - 1] == Recorded(tenantId);
    }
  }
}

// Citation: "await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");" (plan T10 S1c, `ProvisioningWithoutADirectory_RefusesBeforeConnecting`, generalised to the key)
lemma P2_WithoutTheKeyOrTheDirectoryProvisioningRefusesBeforeConnecting(repo: ProvRepo, tenantId: CsString, table: EngTable, failAt: Option<nat>)
  requires IsValidTenant(tenantId)
  requires repo.ProvisionerCs.Some? && (repo.TenantLoginKey.None? || !repo.HasDirectory)
  ensures Provision(repo, tenantId, table, failAt) == (ProvFault(InvalidOperation), [])
{
}

// Citation: "**Provisioning** (`EnsureTenantProvisionedAsync`, as `iverson_provisioner` with `user_admin` active, after the tenant role exists):" / "`CREATE USER IF NOT EXISTS 'iverson_t_…'@'%' IDENTIFIED BY '<derived>' DEFAULT ROLE '<tenant role>'`;" / "`ALTER USER … SET PROPERTIES ('session.query_timeout'='180', 'session.exec_mem_limit'='4294967296')`, the same caps `iverson_app` carries." / "`GRANT <role> TO USER 'iverson_app'@'%'` (`EngagementRepository.cs:523`) is removed." (spec §7.1)
lemma P3_TheTenantLoginReplacesTheAppGrant(repo: ProvRepo, tenantId: CsString, table: EngTable)
  requires IsValidTenant(tenantId)
  requires repo.ProvisionerCs.Some? && repo.TenantLoginKey.Some? && repo.HasDirectory
  ensures var E := Provision(repo, tenantId, table, None).1;
    Ran("GRANT `" + RoleName(tenantId) + "` TO USER 'iverson_app'@'%'") !in E &&
    exists i, j, k :: 0 <= i < j < k < |E| &&
      E[i] == Ran("CREATE ROLE IF NOT EXISTS `" + RoleName(tenantId) + "`") &&
      E[j] == Ran("CREATE USER IF NOT EXISTS '" + LoginName(tenantId) + "'@'%' IDENTIFIED BY '" +
                  LoginPassword(tenantId, repo.TenantLoginKey.value) + "' DEFAULT ROLE '" + RoleName(tenantId) + "'") &&
      E[k] == Ran("ALTER USER '" + LoginName(tenantId) + "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296')")
{
  var loginPassword := LoginPassword(tenantId, repo.TenantLoginKey.value);
  var loginName := LoginName(tenantId);
  var dbName := DatabaseName(tenantId);
  var roleName := RoleName(tenantId);
  var T := TryBlock(dbName, roleName, loginName, loginPassword);
  var all := BeforeTry() + T + FinallyBlock;
  var E := Provision(repo, tenantId, table, None).1;
  assert ProvisionBody(dbName, roleName, loginName, loginPassword, None) == (Ok(true), all);
  assert E == all + [Recorded(tenantId)];
  assert |E| == 15;

  // The three statements, at indices 3, 6 and 7.
  assert E[3] == T[1];
  assert E[6] == T[4];
  assert E[7] == T[5];
  SessionPropertiesTail();
  var head: seq<char> := "ALTER USER '" + loginName;
  assert head + "'@'%' SET PROPERTIES (" + TenantSessionProperties + ")" ==
         head + ("'@'%' SET PROPERTIES (" + TenantSessionProperties + ")");
  assert E[7] == Ran("ALTER USER '" + LoginName(tenantId) + "'@'%' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296')");

  // The grant to iverson_app is none of them.
  var g: seq<char> := "GRANT `" + roleName + "` TO USER 'iverson_app'@'%'";
  assert g[0] == 'G' && g[6] == '`';
  forall e | e in E ensures e != Ran(g)
  {
    if e.Ran? {
      var s := e.sql;
      if s == T[2].sql || s == T[3].sql || s == T[7].sql {
        assert s[6] != '`';
        DiffersAt(s, g, 6);
      } else if s == T[6].sql {
        assert |s| != |g|;
      } else {
        assert s[0] != 'G';
        DiffersAt(s, g, 0);
      }
    }
  }
}
