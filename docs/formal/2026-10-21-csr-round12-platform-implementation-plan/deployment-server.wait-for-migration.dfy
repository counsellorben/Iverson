// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T7 S3
// Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml the wait-for-migration init container's python -c program (64–121)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/deployment-server.wait-for-migration.dfy

// ---------------------------------------------------------------------------
// Library boundary (approved axioms)
// ---------------------------------------------------------------------------

// authentik_version() (lines 79, 100, 116, 118)
type Version(==)

// authentik_build_hash() (lines 79, 100)
type Build(==)

// ---------------------------------------------------------------------------
// Model of what one probe observes
// ---------------------------------------------------------------------------

// tableExists: to_regclass('authentik_version_history') IS NOT NULL (line 94)
// rows:        the (version, build) pairs in authentik_version_history (lines 97-100)
// lock1000:    a pg_locks row for advisory key 1000 in this database, held or awaited (lines 84-88, 104)
// unapplied:   MigrationExecutor(connection).migration_plan(leaf_nodes()) is non-empty,
//              i.e. some of this image's Django migrations are unapplied (lines 108-109)
datatype DbState = DbState(tableExists: bool, rows: set<(Version, Build)>, lock1000: bool, unapplied: bool)

// One call of migrated() (lines 90-111): it returns from the database, or it
// raises (connect, SET search_path, execute, fetchone, the psycopg close at the
// end of the `with`, MigrationExecutor, migration_plan, or connection.close()).
datatype Probe = Answered(db: DbState) | Raised

datatype WaitResult = Finished(at: nat) | StillWaiting

datatype MigratorPhase = NotStarted | LockTaken | RowWritten | Migrating | PassEnded | DiedAfterRow

// The migrator's order (VA72) and what its pass leaves applied; approved axiom.
function DbDuring(before: DbState, p: MigratorPhase, v: Version, b: Build): DbState
  ensures DbDuring(before, p, v, b).tableExists == (before.tableExists || p == RowWritten || p == Migrating || p == PassEnded || p == DiedAfterRow)
  ensures ((v, b) in DbDuring(before, p, v, b).rows) <==> (p == RowWritten || p == Migrating || p == PassEnded || p == DiedAfterRow)
  ensures DbDuring(before, p, v, b).lock1000 <==> (p == LockTaken || p == RowWritten || p == Migrating)
  ensures DbDuring(before, p, v, b).unapplied <==> (before.unapplied && p != PassEnded)

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// def migrated():                                                               (90)
//   with connect(**postgresql_direct_connection_kwargs(CONFIG), autocommit=True) as conn:   (91)
//     cur = conn.cursor(); cur.execute("SET search_path = ...")                  (92-93)
function Migrated(db: DbState, v: Version, b: Build): bool
{
  // cur.execute("SELECT to_regclass('authentik_version_history') IS NOT NULL")   (94)
  // if not cur.fetchone()[0]: return False                                        (95-96)
  if !db.tableExists then false
  // cur.execute("SELECT EXISTS (... WHERE version = %s AND build = %s)", (v, b))  (97-101)
  // if not cur.fetchone()[0]: return False                                        (102-103)
  else if (v, b) !in db.rows then false
  // cur.execute(LOCK_1000)                                                        (104)
  // if cur.fetchone()[0]: return False                                            (105-106)
  else if db.lock1000 then false
  // try:                                                                          (107)
  //   executor = MigrationExecutor(connection)                                    (108)
  //   return not executor.migration_plan(executor.loader.graph.leaf_nodes())      (109)
  // finally: connection.close()                                                   (110-111)
  else !db.unapplied
}

// while True:                                                                    (113)
//   try:                                                                         (114)
//     if migrated(): print("database migrated for", ...); break                  (115-117)
//     print("waiting for the worker to migrate to", ...)                         (118)
//   except Exception as exc: print("waiting for the database:", exc)             (119-120)
//   time.sleep(5)                                                                (121)
// Iteration i consumes probes[i]; the result is the iteration at which the
// loop breaks, or StillWaiting if it has not broken within the given probes.
function WaitFrom(probes: seq<Probe>, v: Version, b: Build, i: nat): WaitResult
  decreases |probes| - i
{
  if i >= |probes| then StillWaiting
  else
    match probes[i]
    case Answered(db) =>
      if Migrated(db, v, b) then Finished(i)
      else WaitFrom(probes, v, b, i + 1)
    case Raised =>
      WaitFrom(probes, v, b, i + 1)
}

function Wait(probes: seq<Probe>, v: Version, b: Build): WaitResult
{
  WaitFrom(probes, v, b, 0)
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

lemma WaitFromFinished(probes: seq<Probe>, v: Version, b: Build, i: nat, k: nat)
  requires WaitFrom(probes, v, b, i) == Finished(k)
  ensures i <= k < |probes| && probes[k].Answered? && Migrated(probes[k].db, v, b)
  ensures forall j :: i <= j < k ==> !(probes[j].Answered? && Migrated(probes[j].db, v, b))
  decreases |probes| - i
{
  assert i < |probes|;
  match probes[i]
  case Answered(db) =>
    if Migrated(db, v, b) {
      assert k == i;
    } else {
      WaitFromFinished(probes, v, b, i + 1, k);
    }
  case Raised =>
    WaitFromFinished(probes, v, b, i + 1, k);
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: plan check `t7_wait_probe.sh` (T7 S1): "A first wait must keep waiting with no version table, with only an older version's row, with this image's row while lock 1000 is held, and with the lock released but none of this image's migrations applied (what a migrator killed after writing its row leaves); it finishes once a real migrator, `python -m lifecycle.migrate`, completes", generalised
lemma P1_MigratedExactly(db: DbState, v: Version, b: Build)
  ensures Migrated(db, v, b) <==> (db.tableExists && (v, b) in db.rows && !db.lock1000 && !db.unapplied)
{
}

// Citation: as P1; "Servers wait for the migration and never migrate." (§2.1 line 59)
lemma P2_WaitEndsAtTheFirstMigratedAnswer(probes: seq<Probe>, v: Version, b: Build, k: nat)
  requires Wait(probes, v, b) == Finished(k)
  ensures k < |probes| && probes[k].Answered? && Migrated(probes[k].db, v, b)
       && forall j :: 0 <= j < k ==> !(probes[j].Answered? && Migrated(probes[j].db, v, b))
{
  WaitFromFinished(probes, v, b, 0, k);
}

// Citation: "Neither the migrator's advisory lock nor the `authentik_version_history` marker shows completion on its own … The plan picks a wait that observes the end of the pass." (§2.1 line 59), for an image that brings Django migrations the database lacks
lemma P3_WaitObservesTheEndOfThePass(before: DbState, p: MigratorPhase, v: Version, b: Build)
  requires (v, b) !in before.rows && !before.lock1000
  requires before.unapplied
  requires Migrated(DbDuring(before, p, v, b), v, b)
  ensures p == PassEnded
{
  var after := DbDuring(before, p, v, b);
  assert (v, b) in after.rows && !after.lock1000 && !after.unapplied;
}
