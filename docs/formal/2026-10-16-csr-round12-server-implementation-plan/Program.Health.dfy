// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T9 S5
// Source: Iverson.Server/Iverson.Api/Program.cs the `/health` handler body (5h, first hunk, with the unchanged tail that picks 200 or 503)
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/Program.Health.dfy

datatype Option<T> = None | Some(value: T)

// EngagementHealthStatus (Iverson.StarRocks/EngagementHealthStatus.cs)
datatype SrStatus = Healthy | AuthPending | Unhealthy

// A task's IsCompletedSuccessfully and Result, as the ContinueWith lambdas read them.
datatype Probe<T> = Answered(value: T) | Faulted

datatype HealthResponse = HealthResponse(Code: int, FullyHealthy: bool)

// ReadinessResult(bool Ready, bool FullyHealthy)
datatype ReadinessResult = ReadinessResult(Ready: bool, FullyHealthy: bool)

// ReadinessPolicy.Evaluate (Iverson.Api/ReadinessPolicy.cs, existing code)
function Evaluate(postgresHealthy: bool, starRocksStatus: SrStatus, qdrantHealthy: bool, kafkaHealthy: bool, engagementEnabled: bool): ReadinessResult
{
  if !engagementEnabled then
    var readyWithoutEngagement := postgresHealthy && qdrantHealthy && kafkaHealthy;
    ReadinessResult(readyWithoutEngagement, readyWithoutEngagement)
  else
    var ready := postgresHealthy && qdrantHealthy && kafkaHealthy
      && starRocksStatus != Unhealthy;
    var fullyHealthy := ready && starRocksStatus == Healthy;
    ReadinessResult(ready, fullyHealthy)
}

// pgTask: t => t.IsCompletedSuccessfully && t.Result == 1
function PgCheck(pg: Probe<int>): bool
{
  pg.Answered? && pg.value == 1
}

// grantsTask: t => t.IsCompletedSuccessfully && t.Result
function GrantsCheck(grants: Probe<bool>): bool
{
  grants.Answered? && grants.value
}

// roleTask: t => t.IsCompletedSuccessfully
function RoleCheck(role: Probe<()>): bool
{
  role.Answered?
}

// The /health handler body. `cached` is the 2 s IMemoryCache entry "health-composite".
function Health(cached: Option<HealthResponse>, pg: Probe<int>, grants: Probe<bool>, role: Probe<()>, registryLoaded: bool, sr: SrStatus, qdrant: bool, kafka: bool, engagementEnabled: bool): HealthResponse
{
  if cached.Some? then
    cached.value
  else
    var pgResult := PgCheck(pg);
    var grantsResult := GrantsCheck(grants);
    var roleResult := RoleCheck(role);
    var srStatus := sr;
    var readiness := Evaluate(
      pgResult && grantsResult && roleResult && registryLoaded,
      srStatus, qdrant, kafka, engagementEnabled);
    var result :=
      if readiness.Ready
      then HealthResponse(200, readiness.FullyHealthy)
      else HealthResponse(503, false);
    result
}

// Citation: "`/health` reports not-ready while any of these hold:" / "`has_table_privilege('iverson_api', '"IversonReconciliationQueue"', 'INSERT')` is false;" / "the registry hasn't loaded;" / "a `SET LOCAL ROLE iverson_runtime` probe fails." (spec §6.3)
lemma P1_ReadinessWaitsForTheGrantsTheRuntimeRoleAndTheRegistry(pg: Probe<int>, grants: Probe<bool>, role: Probe<()>, registryLoaded: bool, sr: SrStatus, qdrant: bool, kafka: bool, engagementEnabled: bool)
  requires grants != Answered(true) || role.Faulted? || !registryLoaded
  ensures Health(None, pg, grants, role, registryLoaded, sr, qdrant, kafka, engagementEnabled).Code == 503
{
}

// Citation: "`ReadinessPolicy` keeps its `AuthPending` tolerance." (spec §6.3)
lemma P2_StarRocksAuthPendingKeepsTheApiReady(engagementEnabled: bool)
  ensures Health(None, Answered(1), Answered(true), Answered(()), true, AuthPending, true, true, engagementEnabled).Code == 200
{
}
