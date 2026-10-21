// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a782ac7674fde9185ad55bb1a986df3008adf050
// Source: §2.5 "Throttle" and "How the counts work", lines 146-160
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/LoginThrottle.dfy
// Round: 2

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

// VA22: "scores clamp at the tenant's −5/+5 (tenants/models.py:34-35)"; VA93: successful logins left the row at +1, +2 and +3
function ClampedAdd(score: int, delta: int): int
  ensures ClampedAdd(score, delta) == if score + delta < -5 then -5 else if score + delta > 5 then 5 else score + delta

// VA65: a plan from row 0 "was cancelled at the 5th wrong password by the password stage's failed_attempts_before_cancel (5)"; plans from −3 and −4 "accepted 12 wrong passwords without cancelling"; VA91: an unknown identifier's row read −1 after identification and the plan was not cancelled through 12 wrong passwords; spec line 158: "counted as the drop in the username's score since the password stage was first shown in that plan"
predicate PasswordStageCancels(promptScore: int, current: int)
  ensures PasswordStageCancels(promptScore, current) <==> promptScore - current >= 5

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// Lines 147-149: `iverson-login-throttle` returns true when the attempt must
// be refused. Cases in the spec's order: pair (line 148), then address
// (line 149).
// pairScore: the summed score of the client address's rows whose identifier
//   is the pending user's username or email (line 148).
// addressNegativeSum: the sum of all negative row scores for the client
//   address (line 149).
// Line 158: "The deny stage runs once per login plan, when identification
// completes", so this is evaluated once per plan, at order 12.
predicate RefusesNewPlan(pairScore: int, addressNegativeSum: int)
{
  pairScore <= -5                  // line 148: pair rule
  || addressNegativeSum <= -50     // line 149: address rule
}

// Line 149: "the sum of all negative row scores for the client address".
// rows: one score per (address, identifier) row for the client address.
function AddressNegativeSum(rows: seq<int>): int
  decreases |rows|
{
  if |rows| == 0 then 0
  else (if rows[0] < 0 then rows[0] else 0) + AddressNegativeSum(rows[1..])
}

// Line 159: the number of identifiers (rows) that carry failures, i.e. have
// a negative score.
function CountNegative(rows: seq<int>): nat
  decreases |rows|
{
  if |rows| == 0 then 0
  else (if rows[0] < 0 then 1 else 0) + CountNegative(rows[1..])
}

// Line 158: one login plan for a known username, from one address, all inside
// the row's hour (line 160: expiry is not modelled; every attempt is taken to
// fall inside the row's hour, as line 158's "in the row's hour" assumes).
// Cases in line 158's order:
//   SuccessfulLogin   - "a successful login raises its username's row by 1"
//   WrongPasswordPlan - a plan that fails with one wrong password
//                       ("5 failed attempts ... (wrong passwords;")
//   WrongCodePlan     - a correct password, then a wrong MFA code
datatype Attempt = SuccessfulLogin | WrongPasswordPlan | WrongCodePlan

// Line 158: the pair's score after one plan of kind a, from score p.
function Step(p: int, a: Attempt): int
{
  match a
  // "a successful login raises its username's row by 1" (clamped at +5,
  // line 158 "found the row below +5"; VA22, VA93)
  case SuccessfulLogin => ClampedAdd(p, 1)
  // one failed attempt lowers the row by 1, clamped at −5 (line 158)
  case WrongPasswordPlan => ClampedAdd(p, -1)
  // "the validation stage and its login-failure signal each score −1"
  case WrongCodePlan => ClampedAdd(ClampedAdd(p, -1), -1)
}

// Line 158: the pair's score after the plans of s, in order, from s0.
function PairAfter(s0: int, s: seq<Attempt>): int
  decreases |s|
{
  if |s| == 0 then s0 else PairAfter(Step(s0, s[0]), s[1..])
}

// n plans of the same kind, one after another.
function Repeat(a: Attempt, n: nat): seq<Attempt>
  decreases n
{
  if n == 0 then [] else [a] + Repeat(a, n - 1)
}

// The number of plans of kind a in s.
function CountOf(a: Attempt, s: seq<Attempt>): nat
  decreases |s|
{
  if |s| == 0 then 0 else (if s[0] == a then 1 else 0) + CountOf(a, s[1..])
}

// Line 158: every plan of s passes order 12, i.e. the deny stage does not
// refuse it, evaluated on the pair's score when that plan reaches order 12.
// The address holds only this pair's row, so the address sum is that row's
// negative part (line 149).
predicate Admissible(s0: int, s: seq<Attempt>)
  decreases |s|
{
  |s| == 0
  || (!RefusesNewPlan(s0, AddressNegativeSum([s0])) && Admissible(Step(s0, s[0]), s[1..]))
}

// Line 158: "Password retries inside a plan that has passed order 12": the
// row after k wrong passwords of `perAttempt` each from s0, clamped at −5
// (VA22, via ClampedAdd).
function ScoreAfterFailures(s0: int, k: nat, perAttempt: nat): int
  decreases k
{
  if k == 0 then s0
  else ClampedAdd(ScoreAfterFailures(s0, k - 1, perAttempt), -(perAttempt as int))
}

// Line 158, unknown username: "its identification's −1 is written before
// order 12 runs". The row after one plan, from p: identification scores −1;
// then, if order 12 does not refuse the plan, its login fails and scores −1
// ("identification and login both fail", −2 per attempt).
function UnknownPlanStep(p: int): int
{
  var afterIdent := ClampedAdd(p, -1);
  if RefusesNewPlan(afterIdent, AddressNegativeSum([afterIdent])) then afterIdent
  else ClampedAdd(afterIdent, -1)
}

// The unknown pair's row after j complete plans from 0.
function UnknownPairAfterPlans(j: nat): int
  decreases j
{
  if j == 0 then 0 else UnknownPlanStep(UnknownPairAfterPlans(j - 1))
}

// Line 158: the unknown pair's score when its k-th plan (k counted from 1)
// reaches order 12: k − 1 earlier plans, then this plan's identification −1.
// k == 0 names no plan; it returns the starting score 0.
function UnknownPairAtOrder12(k: nat): int
{
  if k == 0 then 0 else ClampedAdd(UnknownPairAfterPlans(k - 1), -1)
}

// Line 158: "identification's −1 is already in the stage's starting score":
// the unknown username's score when the password stage is first shown in a
// plan that starts with the pair at p0.
function UnknownPromptScore(p0: int): int
{
  ClampedAdd(p0, -1)
}

// ---------------------------------------------------------------------------
// Helper lemmas (proof repair; each contract is proved)
// ---------------------------------------------------------------------------

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

lemma ScoreAfterFailuresBounds(s0: int, k: nat, d: nat)
  requires -5 <= s0 <= 5
  ensures -5 <= ScoreAfterFailures(s0, k, d) <= s0
  decreases k
{
  if k > 0 {
    ScoreAfterFailuresBounds(s0, k - 1, d);
  }
}

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

lemma SingleRowSum(p: int)
  ensures AddressNegativeSum([p]) == (if p < 0 then p else 0)
{
  assert [p][1..] == [];
}

lemma PairAfterConcat(p: int, a: seq<Attempt>, b: seq<Attempt>)
  ensures PairAfter(p, a + b) == PairAfter(PairAfter(p, a), b)
  decreases |a|
{
  if |a| == 0 {
    assert a + b == b;
  } else {
    assert (a + b)[0] == a[0];
    assert (a + b)[1..] == a[1..] + b;
    PairAfterConcat(Step(p, a[0]), a[1..], b);
  }
}

lemma RepeatWrongPassword(p: int, n: nat)
  requires -5 <= p <= 5
  ensures PairAfter(p, Repeat(WrongPasswordPlan, n)) == if p - n < -5 then -5 else p - n
  decreases n
{
  if n > 0 {
    var r := Repeat(WrongPasswordPlan, n);
    assert r[0] == WrongPasswordPlan;
    assert r[1..] == Repeat(WrongPasswordPlan, n - 1);
    RepeatWrongPassword(ClampedAdd(p, -1), n - 1);
  }
}

lemma RepeatWrongCode(p: int, n: nat)
  requires -5 <= p <= 5
  ensures PairAfter(p, Repeat(WrongCodePlan, n)) == if p - 2 * n < -5 then -5 else p - 2 * n
  decreases n
{
  if n > 0 {
    var r := Repeat(WrongCodePlan, n);
    assert r[0] == WrongCodePlan;
    assert r[1..] == Repeat(WrongCodePlan, n - 1);
    RepeatWrongCode(Step(p, WrongCodePlan), n - 1);
  }
}

lemma RepeatSuccess(p: int, k: nat)
  requires -5 <= p <= 5
  ensures PairAfter(p, Repeat(SuccessfulLogin, k)) == if p + k > 5 then 5 else p + k
  decreases k
{
  if k > 0 {
    var r := Repeat(SuccessfulLogin, k);
    assert r[0] == SuccessfulLogin;
    assert r[1..] == Repeat(SuccessfulLogin, k - 1);
    RepeatSuccess(ClampedAdd(p, 1), k - 1);
  }
}

// The owner signs in, then one wrong password, N times over.
function Interleaved(N: nat): seq<Attempt>
  decreases N
{
  if N == 0 then [] else [SuccessfulLogin, WrongPasswordPlan] + Interleaved(N - 1)
}

lemma InterleavedFacts(N: nat)
  ensures Admissible(0, Interleaved(N))
  ensures CountOf(WrongPasswordPlan, Interleaved(N)) == N
  decreases N
{
  if N > 0 {
    InterleavedFacts(N - 1);
    var s := Interleaved(N);
    var rest := Interleaved(N - 1);
    assert s[0] == SuccessfulLogin;
    assert s[1..] == [WrongPasswordPlan] + rest;
    assert s[1..][0] == WrongPasswordPlan;
    assert s[1..][1..] == rest;
    SingleRowSum(0);
    SingleRowSum(1);
    assert Step(0, SuccessfulLogin) == 1;
    assert Step(1, WrongPasswordPlan) == 0;
    assert Admissible(1, s[1..]);
    assert CountOf(WrongPasswordPlan, s[1..]) == 1 + CountOf(WrongPasswordPlan, rest);
  }
}

lemma UnknownAfterPlansExact(j: nat)
  ensures UnknownPairAfterPlans(j) == if j == 0 then 0 else if j == 1 then -2 else if j == 2 then -4 else -5
  decreases j
{
  if j > 0 {
    UnknownAfterPlansExact(j - 1);
    var p := UnknownPairAfterPlans(j - 1);
    var afterIdent := ClampedAdd(p, -1);
    SingleRowSum(afterIdent);
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "after 5 failed attempts for a known username whose pair starts at 0 (wrong passwords;" (§2.5 line 158)
lemma P1_KnownWrongPasswordsFromZero(n: nat)
  ensures RefusesNewPlan(PairAfter(0, Repeat(WrongPasswordPlan, n)), 0) <==> n >= 5
{
  RepeatWrongPassword(0, n);
}

// Citation: "a wrong MFA code after a correct password lowers the row by 2, since the validation stage and its login-failure signal each score −1, so three wrong codes reach −5" (§2.5 line 158)
lemma P2_ThreeWrongCodesReachMinusFive(n: nat)
  ensures RefusesNewPlan(PairAfter(0, Repeat(WrongCodePlan, n)), 0) <==> n >= 3
{
  RepeatWrongCode(0, n);
}

// Citation: "after 5 wrong passwords plus one more for each successful login from that address in the row's hour that found the row below +5: up to 10 when those logins come before the failures" (§2.5 line 158)
lemma P3_SuccessesFirstOffset(k: nat, n: nat)
  ensures RefusesNewPlan(PairAfter(0, Repeat(SuccessfulLogin, k) + Repeat(WrongPasswordPlan, n)), 0)
          <==> n >= 5 + (if k < 5 then k else 5)
{
  PairAfterConcat(0, Repeat(SuccessfulLogin, k), Repeat(WrongPasswordPlan, n));
  RepeatSuccess(0, k);
  RepeatWrongPassword(PairAfter(0, Repeat(SuccessfulLogin, k)), n);
}

// Citation: "and more when the owner signs in between them" (§2.5 line 158); "and with no fixed bound when the owner keeps signing in between them" (§7 line 538)
lemma P4_InterleavedNoFixedBound(N: nat)
  ensures exists s: seq<Attempt> :: Admissible(0, s) && CountOf(WrongPasswordPlan, s) >= N
{
  InterleavedFacts(N);
  var s := Interleaved(N);
  assert Admissible(0, s) && CountOf(WrongPasswordPlan, s) >= N;
}

// Citation: "An unknown one scores −2 per attempt (identification and login both fail), and its identification's −1 is written before order 12 runs, so its 3rd plan is refused after 2 failed attempts" (§2.5 line 158)
lemma P5_UnknownThirdPlanRefused(k: nat)
  requires k >= 1
  ensures RefusesNewPlan(UnknownPairAtOrder12(k), 0) <==> k >= 3
{
  UnknownAfterPlansExact(k - 1);
}

// Citation: "For a known username, a plan that starts with the pair at 0 is cancelled at its 5th wrong password" (§2.5 line 158)
lemma P6_PlanFromZeroCancelledAtFifth(k: nat)
  ensures PasswordStageCancels(0, ScoreAfterFailures(0, k, 1)) <==> k >= 5
{
  ScoreAfterFailuresExact(0, k, 1);
}

// Citation: "one that starts with the pair between −1 and −4 is never cancelled by the password stage, because the clamp keeps the drop below 5" (§2.5 line 158)
lemma P7_PlanFromNegativeNeverCancelled(s0: int, k: nat)
  requires -4 <= s0 <= -1
  ensures !PasswordStageCancels(s0, ScoreAfterFailures(s0, k, 1))
{
  ScoreAfterFailuresBounds(s0, k, 1);
}

// Citation: "For an unknown username the password stage never cancels a plan: identification's −1 is already in the stage's starting score, so the drop stays below 5 even from a pair at 0" (§2.5 line 158)
lemma P8_UnknownNeverCancelled(p0: int, k: nat)
  requires -4 <= p0 <= 0
  ensures !PasswordStageCancels(UnknownPromptScore(p0), ScoreAfterFailures(UnknownPromptScore(p0), k, 1))
{
  ScoreAfterFailuresBounds(UnknownPromptScore(p0), k, 1);
}

// Citation: "The address cap is a sum over rows, each floored at −5, so it is reached only once failures span at least 10 identifiers." (§2.5 line 159)
lemma P9_AddressCapNeedsTenIdentifiers(rows: seq<int>)
  requires forall i :: 0 <= i < |rows| ==> -5 <= rows[i] <= 5
  requires AddressNegativeSum(rows) <= -50
  ensures CountNegative(rows) >= 10
{
  NegativeSumLowerBound(rows);
}
