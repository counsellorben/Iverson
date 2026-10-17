// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T11 S3
// Source: Iverson.Server/Iverson.StarRocks/StarRocksResiliencePipelineFactory.cs `IsBackendTransient`, `IsPoolExhausted`, and the two `ShouldHandle` predicates in `Build`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/StarRocksResiliencePipelineFactory.IsBackendTransient.dfy

// C# `string` (UTF-16 code units under --unicode-char false); Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

// A MySqlConnector MySqlException: ErrorCode as its int value, Message (non-nullable string).
datatype MySqlEx = MySqlEx(code: int, message: CsString)

// MySqlErrorCode.UnableToConnectToHost.
const UnableToConnectToHost: int := 1042

// Approved axiom: MySqlException.IsTransient, a library-boundary property.
predicate IsTransient(e: MySqlEx)

// string.Contains(sub, StringComparison.Ordinal).
predicate ContainsOrdinal(s: seq<char>, sub: seq<char>)
{
  exists i {:trigger s[i..i + |sub|]} :: 0 <= i <= |s| - |sub| && s[i..i + |sub|] == sub
}

// StarRocksResiliencePipelineFactory.cs:52-54
//   private static bool IsPoolExhausted(MySqlException ex) =>
//       ex.ErrorCode == MySqlErrorCode.UnableToConnectToHost &&
//       ex.Message.Contains("All pooled connections are in use", StringComparison.Ordinal);
predicate IsPoolExhausted(e: MySqlEx)
{
  e.code == UnableToConnectToHost &&
  ContainsOrdinal(e.message, "All pooled connections are in use")
}

// StarRocksResiliencePipelineFactory.cs:47
//   internal static bool IsBackendTransient(MySqlException ex) => ex.IsTransient && !IsPoolExhausted(ex);
function IsBackendTransient(e: MySqlEx): bool
{
  IsTransient(e) && !IsPoolExhausted(e)
}

// StarRocksResiliencePipelineFactory.cs:37 (retry, AddRetry)
//   ShouldHandle = new PredicateBuilder().Handle<MySqlException>(IsBackendTransient)
// For an outcome whose exception is a MySqlException, the predicate is applied to it.
function RetryHandles(e: MySqlEx): bool
{
  IsBackendTransient(e)
}

// StarRocksResiliencePipelineFactory.cs:19 (circuit breaker, AddCircuitBreaker)
//   ShouldHandle = new PredicateBuilder().Handle<MySqlException>(IsBackendTransient)
function BreakerHandles(e: MySqlEx): bool
{
  IsBackendTransient(e)
}

// Citation: "Pool exhaustion ("All pooled connections are in use") is excluded from the transient classification that feeds the per-process circuit breaker, so one tenant can't open the breaker for all tenants." (spec §7.2)
lemma P1_AFullPoolIsNotTransient(e: MySqlEx)
  requires e.code == UnableToConnectToHost && ContainsOrdinal(e.message, "All pooled connections are in use")
  ensures !IsBackendTransient(e)
{
}

// Citation: "await Assert.ThrowsAsync<BrokenCircuitException>(call);" (plan T11 S1b, `Build_StillTripsBreaker_OnAConnectTimeoutWithAFreePool`, generalised)
lemma P2_EveryOtherFailureKeepsItsTransientClassification(e: MySqlEx)
  requires !(e.code == UnableToConnectToHost && ContainsOrdinal(e.message, "All pooled connections are in use"))
  ensures IsBackendTransient(e) == IsTransient(e)
{
}

// Citation: "Pool exhaustion is excluded from the retry as well as the circuit breaker (one predicate feeds both)." (spec §7.3)
lemma P3_OnePredicateFeedsTheRetryAndTheBreaker(e: MySqlEx)
  ensures RetryHandles(e) == IsBackendTransient(e) && BreakerHandles(e) == IsBackendTransient(e)
{
}
