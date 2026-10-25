// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T15 S4
// Source: Iverson.Server/Iverson.StarRocks/EngagementHealthChecker.cs CheckHealthAsync (12–30), ClassifyConnectionException (42–45), CreateBackendProbe (60–61), AnyBackendReadyAsync (73–83)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/EngagementHealthChecker.CheckHealthAsync.dfy

// ---------------------------------------------------------------------------
// Model (approval list) and library boundary
// ---------------------------------------------------------------------------

// MySqlException.ErrorCode values the code distinguishes; NotMySql is any non-MySqlException.
// CommandTimeout is MySqlConnector's MySqlException { ErrorCode: CommandTimeoutExpired }.
datatype MySqlError = AccessDenied | OtherError(code: int) | CommandTimeout | NotMySql
// Outcome of conn.OpenAsync() (line 17) and of the "SELECT 1" ExecuteScalarAsync (line 20).
datatype Step = StepOk | StepFails(err: MySqlError)
// Outcome of the backend probe's ExecuteReaderAsync + ReadAsync loop (lines 76, 79):
// Rows(n) = the reader yields n rows then ReadAsync returns false; ProbeFails(e) = a call throws e.
datatype ProbeResult = Rows(n: nat) | ProbeFails(err: MySqlError)
datatype HealthEnv = HealthEnv(open: Step, selectOne: Step, probe: ProbeResult)
// EngagementHealthStatus
datatype Status = Healthy | AuthPending | Unhealthy

predicate StatementDenied(env: HealthEnv)
{
  env.selectOne == StepFails(AccessDenied) || env.probe == ProbeFails(AccessDenied)
}

// Approved axiom (VA45): the probe's outcome on a working login.
function QueryOutcome(backendAlive: bool): ProbeResult
  ensures QueryOutcome(backendAlive) == (if backendAlive then Rows(2) else ProbeFails(OtherError(1064)))

// C# int (System.Int32); the project is unchecked (no CheckForOverflowUnderflow), so ++ wraps.
newtype Int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

function Inc32(x: Int32): Int32
{
  if x == 0x7fff_ffff then -0x8000_0000 else x + 1
}

// A thrown exception, as a result variant.
datatype Outcome<T> = Ok(value: T) | Throws(err: MySqlError)

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// ClassifyConnectionException (42–45)
function ClassifyConnectionException(ex: MySqlError): Status
{
  if ex == AccessDenied
  then AuthPending
  else Unhealthy
}

// CreateBackendProbe (60–61): builds the command; no branching. Its timeout's effect is the
// ProbeFails(CommandTimeout) outcome of the environment.
const BackendProbeSql: string := "SELECT * FROM TABLE(generate_series(1,2))"
const BackendProbeTimeoutSeconds: Int32 := 5

// while (await reader.ReadAsync(ct)) rows++;   (79–80), `remaining` rows still to be read
function CountLoop(remaining: nat, rows: Int32): Int32
  decreases remaining
{
  if remaining == 0 then rows
  else CountLoop(remaining - 1, Inc32(rows))
}

// AnyBackendReadyAsync (73–83)
function AnyBackendReady(probe: ProbeResult): Outcome<bool>
{
  match probe
  case ProbeFails(e) => Throws(e)                // ExecuteReaderAsync / ReadAsync throws (76, 79)
  case Rows(n) =>
    var rows: Int32 := 0;                        // 78
    var rows' := CountLoop(n, rows);             // 79–80
    Ok(rows' == 2)                               // 82
}

// CheckHealthAsync (12–30)
function CheckHealth(env: HealthEnv): Status
{
  if env.open.StepFails? then ClassifyConnectionException(env.open.err)            // 17 -> 26–28
  else if env.selectOne.StepFails? then ClassifyConnectionException(env.selectOne.err) // 20 -> 26–28
  else
    var r := AnyBackendReady(env.probe);                                            // 22
    if r.Throws? then ClassifyConnectionException(r.err)                             // 22 -> 26–28
    else if r.value then Healthy                                                     // 23
    else Unhealthy                                                                   // 24
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

function Wrap32(x: int): int
{
  ((x + 0x8000_0000) % 0x1_0000_0000) - 0x8000_0000
}

lemma Inc32IsWrap(x: Int32)
  ensures Inc32(x) as int == Wrap32(x as int + 1)
{
}

lemma Wrap32Shift(x: int)
  ensures Wrap32(Wrap32(x) + 1) == Wrap32(x + 1)
{
}

lemma CountLoopIsWrap(n: nat, rows: Int32)
  ensures CountLoop(n, rows) as int == Wrap32(rows as int + n)
  decreases n
{
  if n == 0 {
  } else {
    CountLoopIsWrap(n - 1, Inc32(rows));
    Inc32IsWrap(rows);
    Wrap32Shift(rows as int);
    assert Wrap32(Wrap32(rows as int + 1) + (n - 1)) == Wrap32(rows as int + n) by {
      WrapAdd(rows as int + 1, n - 1);
    }
  }
}

lemma WrapAdd(x: int, k: int)
  ensures Wrap32(Wrap32(x) + k) == Wrap32(x + k)
{
}

// The P3 counterexample: 4,294,967,298 rows wrap the int counter back to 2.
lemma P3_Counterexample()
  ensures CheckHealth(HealthEnv(StepOk, StepOk, Rows(0x1_0000_0002))) == Healthy
{
  CountLoopIsWrap(0x1_0000_0002, 0);
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: design ReadinessMapping.P1_AuthPendingOnlyFromRefusedLogin: "With no grant needed, `AuthPending` now arises only from a refused login." (§3.5 line 317) (the precondition is VA45's fact that the probe needs no grant, so after a login no statement fails with the login error)
lemma P1_AuthPendingOnlyFromRefusedLogin(env: HealthEnv)
  requires !StatementDenied(env)
  ensures CheckHealth(env) == AuthPending <==> env.open == StepFails(AccessDenied)
{
}

// Citation: design ReadinessMapping.P2_RowsMeanHealthy: "Two rows mean Healthy. The query needs no grant, returns rows with a backend alive, and fails in about 0.4 s ("Backend node not found") with none (Verified assumption 45)." (§3.5 line 316)
lemma P2_RowsMeanHealthy(backendAlive: bool)
  ensures CheckHealth(HealthEnv(StepOk, StepOk, QueryOutcome(backendAlive))) == (if backendAlive then Healthy else Unhealthy)
{
  if backendAlive {
    assert CountLoop(2, 0) == 2;
  }
}

// Citation: "Two rows mean Healthy." (§3.5 line 316)
lemma P3_HealthyIsExactlyTwoRows(env: HealthEnv)
  ensures CheckHealth(env) == Healthy <==> (env.open == StepOk && env.selectOne == StepOk && env.probe == Rows(2))
{
  assert CountLoop(2, 0) == 2;
  if env.probe.Rows? {
    CountLoopIsWrap(env.probe.n, 0);
  }
}
