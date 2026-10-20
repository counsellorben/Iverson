// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ 19d74cfb95c4ccb8ceca9e939494828f3359c3a5
// Source: §2.5 "Throttle" and "How the counts work", lines 146-160
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/LoginThrottle.dfy
// Round: 1

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

// VA22: "scores clamp at the tenant's −5/+5 (tenants/models.py:34-35)"; "an unknown username scores −2 per attempt"
function ClampedAdd(score: int, delta: int): int
  ensures ClampedAdd(score, delta) == if score + delta < -5 then -5 else if score + delta > 5 then 5 else score + delta

// VA65: a plan from row 0 "was cancelled at the 5th wrong password by the password stage's failed_attempts_before_cancel (5)"; plans from −3 and −4 "accepted 12 wrong passwords without cancelling"; spec line 158: "counted as the score drop since the plan started"
predicate PasswordStageCancels(planStart: int, current: int)
  ensures PasswordStageCancels(planStart, current) <==> planStart - current >= 5

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// Line 147-149: `iverson-login-throttle` returns true when the attempt must be
// refused. Cases in the spec's order: pair (line 148), then address (line 149).
// pairScore: the summed score of the client address's rows whose identifier is
//   the pending user's username or email (line 148).
// addressNegativeSum: the sum of all negative row scores for the client
//   address (line 149).
// Line 158: the deny stage runs once per login plan, when identification
// completes, so this predicate is evaluated once at the start of a new plan.
predicate RefusesNewPlan(pairScore: int, addressNegativeSum: int)
{
  pairScore <= -5                  // line 148: pair rule
  || addressNegativeSum <= -50     // line 149: address rule
}

// Line 158: each failed attempt lowers the (address, identifier) row by
// `perAttempt` (1 for a known username; 2 for an unknown one, "identification
// and login both fail"), and the row is clamped at −5 (VA22, via ClampedAdd).
// Failures are applied one after another, starting from score s0.
function ScoreAfterFailures(s0: int, k: nat, perAttempt: nat): int
  decreases k
{
  if k == 0 then s0
  else ClampedAdd(ScoreAfterFailures(s0, k - 1, perAttempt), -(perAttempt as int))
}

// Line 158: the pair's score after n failed attempts, starting from no failures
// (score 0).
function PairScoreAfter(n: nat, perAttempt: nat): int
{
  ScoreAfterFailures(0, n, perAttempt)
}

// Line 149: "the sum of all negative row scores for the client address".
// rows: one score per (address, identifier) row for the client address
// (Reputation is unique on (identifier, ip), VA22).
function AddressNegativeSum(rows: seq<int>): int
  decreases |rows|
{
  if |rows| == 0 then 0
  else (if rows[0] < 0 then rows[0] else 0) + AddressNegativeSum(rows[1..])
}

// Line 159: the number of identifiers (rows) that carry failures, i.e. have a
// negative score.
function CountNegative(rows: seq<int>): nat
  decreases |rows|
{
  if |rows| == 0 then 0
  else (if rows[0] < 0 then 1 else 0) + CountNegative(rows[1..])
}

// ---------------------------------------------------------------------------
// Helper lemmas (proof repair; each contract is proved)
// ---------------------------------------------------------------------------

// From a start inside the clamp range, the score after k failures of size d
// is s0 - k*d floored at -5 (expressed without multiplication via a running
// bound: it equals -5 once s0 - k*d would be below -5).
lemma ScoreAfterFailuresExact(s0: int, k: nat, d: nat)
  requires -5 <= s0 <= 5
  ensures ScoreAfterFailures(s0, k, d) == if s0 - k * d < -5 then -5 else s0 - k * d
  decreases k
{
  if k > 0 {
    ScoreAfterFailuresExact(s0, k - 1, d);
    var prev := ScoreAfterFailures(s0, k - 1, d);
    assert ScoreAfterFailures(s0, k, d) == ClampedAdd(prev, -(d as int));
    assert (k - 1) * d + d == k * d;
    if s0 - (k - 1) * d < -5 {
      assert prev == -5;
      assert s0 - k * d < -5;
    } else {
      assert prev == s0 - (k - 1) * d;
      assert prev + -(d as int) == s0 - k * d;
    }
  }
}

// Within the clamp range the score never drops below -5 and never rises
// above its start when the step is a loss.
lemma ScoreAfterFailuresBounds(s0: int, k: nat, d: nat)
  requires -5 <= s0 <= 5
  ensures -5 <= ScoreAfterFailures(s0, k, d) <= s0
  decreases k
{
  if k > 0 {
    ScoreAfterFailuresBounds(s0, k - 1, d);
  }
}

// Each negative row contributes at least -5 to the address sum.
lemma NegativeSumLowerBound(rows: seq<int>)
  requires forall i :: 0 <= i < |rows| ==> -5 <= rows[i] <= 5
  ensures AddressNegativeSum(rows) >= -5 * CountNegative(rows)
  decreases |rows|
{
  if |rows| > 0 {
    assert forall i :: 0 <= i < |rows[1..]| ==> rows[1..][i] == rows[i + 1];
    NegativeSumLowerBound(rows[1..]);
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "the pair rule refuses **new login plans** once the pair's rows reach −5: after 5 failed attempts for a known username" (§2.5 line 158)
lemma P1_KnownUserRefusedFromFifth(n: nat)
  ensures RefusesNewPlan(PairScoreAfter(n, 1), 0) <==> n >= 5
{
  ScoreAfterFailuresExact(0, n, 1);
}

// Citation: "or 3 for an unknown one (it scores −2 per attempt: identification and login both fail)" (§2.5 line 158)
lemma P2_UnknownUserRefusedFromThird(n: nat)
  ensures RefusesNewPlan(PairScoreAfter(n, 2), 0) <==> n >= 3
{
  ScoreAfterFailuresExact(0, n, 2);
}

// Citation: "A plan that starts with the pair at 0 is cancelled at its 5th wrong password" (§2.5 line 158)
lemma P3_PlanFromZeroCancelledAtFifth(k: nat)
  ensures PasswordStageCancels(0, ScoreAfterFailures(0, k, 1)) <==> k >= 5
{
  ScoreAfterFailuresExact(0, k, 1);
}

// Citation: "one that starts with the pair between −1 and −4 is never cancelled by the password stage, because the clamp keeps the drop below 5" (§2.5 line 158)
lemma P4_PlanFromNegativeNeverCancelled(s0: int, k: nat)
  requires -4 <= s0 <= -1
  ensures !PasswordStageCancels(s0, ScoreAfterFailures(s0, k, 1))
{
  ScoreAfterFailuresBounds(s0, k, 1);
}

// Citation: "The address cap is a sum over rows, each floored at −5, so it is reached only once failures span at least 10 identifiers." (§2.5 line 159)
lemma P5_AddressCapNeedsTenIdentifiers(rows: seq<int>)
  requires forall i :: 0 <= i < |rows| ==> -5 <= rows[i] <= 5
  requires AddressNegativeSum(rows) <= -50
  ensures CountNegative(rows) >= 10
{
  NegativeSumLowerBound(rows);
}
