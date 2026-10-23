// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T7 S3
// Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml the wait-for-migration init container's python -c program (64–102)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/deployment-server.wait-for-migration.dfy

// ---------------------------------------------------------------------------
// Library boundary (approved axioms)
// ---------------------------------------------------------------------------

// authentik_version() (lines 66, 87)
type Version(==)

// authentik_build_hash() (lines 66, 87)
type Build(==)

// ---------------------------------------------------------------------------
// Model of what one probe observes
// ---------------------------------------------------------------------------

// tableExists: to_regclass('authentik_version_history') IS NOT NULL (line 81)
// rows:        the (version, build) pairs in authentik_version_history (lines 85-86)
// lock1000:    a pg_locks row for advisory key 1000 in this database, held or awaited (lines 71-75, 91)
datatype DbState = DbState(tableExists: bool, rows: set<(Version, Build)>, lock1000: bool)

// One call of migrated(): it answers from the database, or it raises
// (connect, SET search_path, execute, fetchone or the connection close).
datatype Probe = Answered(db: DbState) | Raised

datatype WaitResult = Finished(at: nat) | StillWaiting

datatype MigratorPhase = NotStarted | LockTaken | RowWritten | Migrating | PassEnded | DiedAfterRow

// The migrator's order (VA72), approved axiom.
function DbDuring(before: DbState, p: MigratorPhase, v: Version, b: Build): DbState
  ensures DbDuring(before, p, v, b).tableExists == (before.tableExists || p == RowWritten || p == Migrating || p == PassEnded || p == DiedAfterRow)
  ensures ((v, b) in DbDuring(before, p, v, b).rows) <==> (p == RowWritten || p == Migrating || p == PassEnded || p == DiedAfterRow)
  ensures DbDuring(before, p, v, b).lock1000 <==> (p == LockTaken || p == RowWritten || p == Migrating)

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// def migrated():                                                   (77)
function Migrated(db: DbState, v: Version, b: Build): bool
{
  // cur.execute("SELECT to_regclass('authentik_version_history') IS NOT NULL")   (81)
  // if not cur.fetchone()[0]: return False                                        (82-83)
  if !db.tableExists then false
  // cur.execute("SELECT EXISTS (... WHERE version = %s AND build = %s)", (v, b))  (84-88)
  // if not cur.fetchone()[0]: return False                                        (89-90)
  else if (v, b) !in db.rows then false
  // cur.execute(LOCK_1000); return not cur.fetchone()[0]                          (91-92)
  else !db.lock1000
}

// while True:                                                       (94)
//   try:
//     if migrated(): print(...); break                              (95-98)
//     print("waiting for the worker ...")                           (99)
//   except Exception as exc: print(...)                             (100-101)
//   time.sleep(5)                                                   (102)
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

// Concrete counterexample to P3: a migrator that died after writing this
// version's row (Postgres then frees its session's advisory lock) leaves the
// database in a state the wait counts as migrated, though the pass never ended.
lemma P3_CounterexampleDiedAfterRow(v: Version, b: Build)
  ensures var before := DbState(false, {}, false);
          (v, b) !in before.rows && !before.lock1000
          && Migrated(DbDuring(before, DiedAfterRow, v, b), v, b)
          && DiedAfterRow != PassEnded
{
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: plan check `t7_wait_probe.sh` (T7 S1): "It must keep waiting with no version table, with only an older version's row, and with this image's row while lock 1000 is held, and finish once the lock is released", generalised
lemma P1_MigratedExactly(db: DbState, v: Version, b: Build)
  ensures Migrated(db, v, b) <==> (db.tableExists && (v, b) in db.rows && !db.lock1000)
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

// Citation: "Neither the migrator's advisory lock nor the `authentik_version_history` marker shows completion on its own … The plan picks a wait that observes the end of the pass." (§2.1 line 59)
lemma P3_WaitObservesTheEndOfThePass(before: DbState, p: MigratorPhase, v: Version, b: Build)
  requires (v, b) !in before.rows && !before.lock1000
  requires Migrated(DbDuring(before, p, v, b), v, b)
  ensures p == PassEnded
{
  var after := DbDuring(before, p, v, b);
  assert (v, b) in after.rows && !after.lock1000;
  // Row present and lock free leaves exactly PassEnded or DiedAfterRow.
  assert p == PassEnded || p == DiedAfterRow;
}
