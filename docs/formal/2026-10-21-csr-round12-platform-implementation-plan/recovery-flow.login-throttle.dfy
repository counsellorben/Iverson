// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T9 S3
// Source: Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/recovery-flow.yaml the iverson-login-throttle expression (71–79)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/recovery-flow.login-throttle.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// Python str: a sequence of code points.
newtype CodePoint = x: int | 0 <= x < 0x110000

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

// Authentik's score update: the design's approved VA22/VA93 clamp.
function ClampedAdd(score: int, delta: int): int
  ensures ClampedAdd(score, delta) == if score + delta < -5 then -5 else if score + delta > 5 then 5 else score + delta

// ---------------------------------------------------------------------------
// Library boundary: Django ORM over the Reputation table
// ---------------------------------------------------------------------------

// One authentik_policies_reputation.Reputation row (ip, identifier, score).
datatype RepRow = RepRow(ip: seq<CodePoint>, identifier: seq<CodePoint>, score: int)

// The pending user: getattr(user, "username", None), getattr(user, "email", None).
datatype PendingUser = PendingUser(username: Option<seq<CodePoint>>, email: Option<seq<CodePoint>>)

// Reputation.objects.filter(ip=ip, identifier__in=identifiers): every row whose
// ip equals ip and whose identifier is a member of identifiers (set membership:
// a row is kept once however often its identifier is listed). Row order is
// irrelevant to Sum; the filter is written over the last row.
function FilterPair(rows: seq<RepRow>, ip: seq<CodePoint>, identifiers: seq<seq<CodePoint>>): seq<RepRow>
  decreases |rows|
{
  if |rows| == 0 then []
  else
    FilterPair(rows[..|rows| - 1], ip, identifiers)
    + (if rows[|rows| - 1].ip == ip && rows[|rows| - 1].identifier in identifiers then [rows[|rows| - 1]] else [])
}

// Reputation.objects.filter(ip=ip, score__lt=0).
function FilterNeg(rows: seq<RepRow>, ip: seq<CodePoint>): seq<RepRow>
  decreases |rows|
{
  if |rows| == 0 then []
  else
    FilterNeg(rows[..|rows| - 1], ip)
    + (if rows[|rows| - 1].ip == ip && rows[|rows| - 1].score < 0 then [rows[|rows| - 1]] else [])
}

// The arithmetic sum of the rows' scores.
function Total(rs: seq<RepRow>): int
  decreases |rs|
{
  if |rs| == 0 then 0 else Total(rs[..|rs| - 1]) + rs[|rs| - 1].score
}

// .aggregate(total=Sum("score"))["total"]: SQL SUM, NULL (None) over no rows.
function SqlSum(rs: seq<RepRow>): Option<int>
{
  if |rs| == 0 then None else Some(Total(rs))
}

// Python `x or 0` on an int-or-None: None and 0 are falsy.
function OrZero(x: Option<int>): int
{
  if x.Some? && x.value != 0 then x.value else 0
}

// Python truthiness of a str-or-None: None and "" are falsy.
predicate Truthy(s: Option<seq<CodePoint>>)
{
  s.Some? && s.value != []
}

// ---------------------------------------------------------------------------
// Transliteration: recovery-flow.yaml lines 71–79
// ---------------------------------------------------------------------------

// line 76: identifiers = [i for i in (getattr(user, "username", None), getattr(user, "email", None)) if i]
function Identifiers(user: PendingUser): seq<seq<CodePoint>>
{
  (if Truthy(user.username) then [user.username.value] else [])
  + (if Truthy(user.email) then [user.email.value] else [])
}

// The expression's return value. clientIp is str(ak_client_ip) (line 74);
// user is request.user (line 75); rows is the Reputation table.
predicate Throttle(rows: seq<RepRow>, clientIp: seq<CodePoint>, user: PendingUser)
{
  var ip := clientIp;                                                     // line 74
  var identifiers := Identifiers(user);                                   // line 76
  var pair := OrZero(SqlSum(FilterPair(rows, ip, identifiers)));          // line 77
  var address := OrZero(SqlSum(FilterNeg(rows, ip)));                    // line 78
  pair <= -5 || address <= -50                                            // line 79
}

// The `address` value of line 78.
function AddressAggregate(rows: seq<RepRow>, ip: seq<CodePoint>): int
{
  OrZero(SqlSum(FilterNeg(rows, ip)))
}

function OneRow(ip: seq<CodePoint>, id: seq<CodePoint>, score: int): seq<RepRow>
{
  [RepRow(ip, id, score)]
}

// ---------------------------------------------------------------------------
// Environment: Authentik's scoring (design model LoginThrottle.dfy)
// ---------------------------------------------------------------------------

datatype Attempt = SuccessfulLogin | WrongPasswordPlan | WrongCodePlan

function Step(p: int, a: Attempt): int
{
  match a
  case SuccessfulLogin => ClampedAdd(p, 1)
  case WrongPasswordPlan => ClampedAdd(p, -1)
  case WrongCodePlan => ClampedAdd(ClampedAdd(p, -1), -1)
}

function PairAfter(s0: int, s: seq<Attempt>): int
  decreases |s|
{
  if |s| == 0 then s0 else PairAfter(Step(s0, s[0]), s[1..])
}

function Repeat(a: Attempt, n: nat): seq<Attempt>
  decreases n
{
  if n == 0 then [] else [a] + Repeat(a, n - 1)
}

function CountOf(a: Attempt, s: seq<Attempt>): nat
  decreases |s|
{
  if |s| == 0 then 0 else (if s[0] == a then 1 else 0) + CountOf(a, s[1..])
}

// Every plan of s passes the deny stage: the expression, on a table holding
// only the pair's username row at its score when the plan reaches order 12,
// returns false.
predicate AdmissibleFor(ip: seq<CodePoint>, u: PendingUser, s0: int, s: seq<Attempt>)
  requires u.username.Some?
  decreases |s|
{
  |s| == 0
  || (!Throttle(OneRow(ip, u.username.value, s0), ip, u) && AdmissibleFor(ip, u, Step(s0, s[0]), s[1..]))
}

// The deny stage's refusal inside an unknown username's plan, decided by the
// expression on a one-row table. UnknownPairAtOrder12 takes no address or
// identifier, so a fixed one is used; OneRowThrottle proves the decision is
// the same for every address, non-empty identifier and email.
const EnvIp: seq<CodePoint> := []
const EnvId: seq<CodePoint> := [0 as CodePoint]

function UnknownPlanStep(p: int): int
{
  var afterIdent := ClampedAdd(p, -1);
  if Throttle(OneRow(EnvIp, EnvId, afterIdent), EnvIp, PendingUser(Some(EnvId), None)) then afterIdent
  else ClampedAdd(afterIdent, -1)
}

function UnknownPairAfterPlans(j: nat): int
  decreases j
{
  if j == 0 then 0 else UnknownPlanStep(UnknownPairAfterPlans(j - 1))
}

function UnknownPairAtOrder12(k: nat): int
{
  if k == 0 then 0 else ClampedAdd(UnknownPairAfterPlans(k - 1), -1)
}

// ---------------------------------------------------------------------------
// Helper lemmas (proof repair; each contract is proved)
// ---------------------------------------------------------------------------

lemma OneRowThrottle(ip: seq<CodePoint>, id: seq<CodePoint>, u: PendingUser, p: int)
  requires id in Identifiers(u)
  ensures Throttle(OneRow(ip, id, p), ip, u) <==> p <= -5
{
  var rows := OneRow(ip, id, p);
  assert rows[..0] == [];
  assert FilterPair(rows, ip, Identifiers(u)) == rows;
  var neg := FilterNeg(rows, ip);
  assert FilterNeg(rows[..0], ip) == [];
  if p < 0 {
    assert neg == [] + rows;
  } else {
    assert neg == [] + [];
  }
  assert Total(rows) == p;
}

lemma TotalAppend(a: seq<RepRow>, x: RepRow)
  ensures Total(a + [x]) == Total(a) + x.score
{
  assert (a + [x])[..|a|] == a;
}

lemma NegTotalBound(rows: seq<RepRow>, ip: seq<CodePoint>)
  requires forall i :: 0 <= i < |rows| ==> -5 <= rows[i].score <= 5
  ensures Total(FilterNeg(rows, ip)) >= -5 * |set i | 0 <= i < |rows| && rows[i].ip == ip && rows[i].score < 0|
  decreases |rows|
{
  if |rows| > 0 {
    var init := rows[..|rows| - 1];
    var last := rows[|rows| - 1];
    NegTotalBound(init, ip);
    var si := set i | 0 <= i < |init| && init[i].ip == ip && init[i].score < 0;
    var sr := set i | 0 <= i < |rows| && rows[i].ip == ip && rows[i].score < 0;
    if last.ip == ip && last.score < 0 {
      assert sr == si + {|rows| - 1};
      assert |rows| - 1 !in si;
      TotalAppend(FilterNeg(init, ip), last);
    } else {
      assert sr == si;
      assert FilterNeg(rows, ip) == FilterNeg(init, ip);
    }
  }
}

lemma FilterPairSubset(rows: seq<RepRow>, ip: seq<CodePoint>, ids: seq<seq<CodePoint>>)
  ensures forall r :: r in FilterPair(rows, ip, ids) ==> r in rows
  decreases |rows|
{
  if |rows| > 0 {
    FilterPairSubset(rows[..|rows| - 1], ip, ids);
    assert forall r :: r in rows[..|rows| - 1] ==> r in rows;
  }
}

lemma TotalNonPos(rs: seq<RepRow>)
  requires forall r :: r in rs ==> r.score <= 0
  ensures Total(rs) <= 0
  decreases |rs|
{
  if |rs| > 0 {
    assert forall r :: r in rs[..|rs| - 1] ==> r in rs;
    TotalNonPos(rs[..|rs| - 1]);
  }
}

lemma PairTotalAtMost(rows: seq<RepRow>, ip: seq<CodePoint>, ids: seq<seq<CodePoint>>, x: RepRow)
  requires x in rows && x.ip == ip && x.identifier in ids
  requires forall r :: r in rows ==> r.score <= 0
  ensures |FilterPair(rows, ip, ids)| > 0
  ensures Total(FilterPair(rows, ip, ids)) <= x.score
  decreases |rows|
{
  var init := rows[..|rows| - 1];
  var last := rows[|rows| - 1];
  assert rows == init + [last];
  assert forall r :: r in init ==> r in rows;
  if x in init {
    PairTotalAtMost(init, ip, ids, x);
    if last.ip == ip && last.identifier in ids {
      TotalAppend(FilterPair(init, ip, ids), last);
    } else {
      assert FilterPair(rows, ip, ids) == FilterPair(init, ip, ids);
    }
  } else {
    assert x == last;
    FilterPairSubset(init, ip, ids);
    TotalNonPos(FilterPair(init, ip, ids));
    TotalAppend(FilterPair(init, ip, ids), last);
  }
}

lemma NoRowsAtIp(rows: seq<RepRow>, ip: seq<CodePoint>, ids: seq<seq<CodePoint>>)
  requires forall r :: r in rows ==> r.ip != ip
  ensures FilterPair(rows, ip, ids) == []
  ensures FilterNeg(rows, ip) == []
  decreases |rows|
{
  if |rows| > 0 {
    assert forall r :: r in rows[..|rows| - 1] ==> r in rows;
    assert rows[|rows| - 1] in rows;
    NoRowsAtIp(rows[..|rows| - 1], ip, ids);
  }
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

lemma InterleavedFacts(ip: seq<CodePoint>, u: PendingUser, N: nat)
  requires u.username.Some? && u.username.value != []
  ensures AdmissibleFor(ip, u, 0, Interleaved(N))
  ensures CountOf(WrongPasswordPlan, Interleaved(N)) == N
  decreases N
{
  if N > 0 {
    InterleavedFacts(ip, u, N - 1);
    var s := Interleaved(N);
    var rest := Interleaved(N - 1);
    assert s[0] == SuccessfulLogin;
    assert s[1..] == [WrongPasswordPlan] + rest;
    assert s[1..][0] == WrongPasswordPlan;
    assert s[1..][1..] == rest;
    OneRowThrottle(ip, u.username.value, u, 0);
    OneRowThrottle(ip, u.username.value, u, 1);
    assert Step(0, SuccessfulLogin) == 1;
    assert Step(1, WrongPasswordPlan) == 0;
    assert AdmissibleFor(ip, u, 1, s[1..]);
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
    OneRowThrottle(EnvIp, EnvId, PendingUser(Some(EnvId), None), afterIdent);
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: design LoginThrottle.P1_KnownWrongPasswordsFromZero: "after 5 failed attempts for a known username whose pair starts at 0 (wrong passwords;" (§2.5 line 158)
lemma P1_KnownWrongPasswordsFromZero(ip: seq<CodePoint>, u: PendingUser, n: nat)
  requires u.username.Some? && u.username.value != []
  ensures Throttle(OneRow(ip, u.username.value, PairAfter(0, Repeat(WrongPasswordPlan, n))), ip, u) <==> n >= 5
{
  RepeatWrongPassword(0, n);
  OneRowThrottle(ip, u.username.value, u, PairAfter(0, Repeat(WrongPasswordPlan, n)));
}

// Citation: design LoginThrottle.P2_ThreeWrongCodesReachMinusFive: "a wrong MFA code after a correct password lowers the row by 2, since the validation stage and its login-failure signal each score −1, so three wrong codes reach −5" (§2.5 line 158)
lemma P2_ThreeWrongCodesReachMinusFive(ip: seq<CodePoint>, u: PendingUser, n: nat)
  requires u.username.Some? && u.username.value != []
  ensures Throttle(OneRow(ip, u.username.value, PairAfter(0, Repeat(WrongCodePlan, n))), ip, u) <==> n >= 3
{
  RepeatWrongCode(0, n);
  OneRowThrottle(ip, u.username.value, u, PairAfter(0, Repeat(WrongCodePlan, n)));
}

// Citation: design LoginThrottle.P3_SuccessesFirstOffset: "after 5 wrong passwords plus one more for each successful login from that address in the row's hour that found the row below +5: up to 10 when those logins come before the failures" (§2.5 line 158)
lemma P3_SuccessesFirstOffset(ip: seq<CodePoint>, u: PendingUser, k: nat, n: nat)
  requires u.username.Some? && u.username.value != []
  ensures Throttle(OneRow(ip, u.username.value, PairAfter(0, Repeat(SuccessfulLogin, k) + Repeat(WrongPasswordPlan, n))), ip, u)
          <==> n >= 5 + (if k < 5 then k else 5)
{
  PairAfterConcat(0, Repeat(SuccessfulLogin, k), Repeat(WrongPasswordPlan, n));
  RepeatSuccess(0, k);
  RepeatWrongPassword(PairAfter(0, Repeat(SuccessfulLogin, k)), n);
  OneRowThrottle(ip, u.username.value, u, PairAfter(0, Repeat(SuccessfulLogin, k) + Repeat(WrongPasswordPlan, n)));
}

// Citation: design LoginThrottle.P4_InterleavedNoFixedBound: "and more when the owner signs in between them" (§2.5 line 158); "and with no fixed bound when the owner keeps signing in between them" (§7 line 538)
lemma P4_InterleavedNoFixedBound(ip: seq<CodePoint>, u: PendingUser, N: nat)
  requires u.username.Some? && u.username.value != []
  ensures exists s: seq<Attempt> :: AdmissibleFor(ip, u, 0, s) && CountOf(WrongPasswordPlan, s) >= N
{
  InterleavedFacts(ip, u, N);
  var s := Interleaved(N);
  assert AdmissibleFor(ip, u, 0, s) && CountOf(WrongPasswordPlan, s) >= N;
}

// Citation: design LoginThrottle.P5_UnknownThirdPlanRefused: "An unknown one scores −2 per attempt (identification and login both fail), and its identification's −1 is written before order 12 runs, so its 3rd plan is refused after 2 failed attempts" (§2.5 line 158)
lemma P5_UnknownThirdPlanRefused(ip: seq<CodePoint>, id: seq<CodePoint>, email: Option<seq<CodePoint>>, k: nat)
  requires k >= 1 && id != []
  ensures Throttle(OneRow(ip, id, UnknownPairAtOrder12(k)), ip, PendingUser(Some(id), email)) <==> k >= 3
{
  UnknownAfterPlansExact(k - 1);
  OneRowThrottle(ip, id, PendingUser(Some(id), email), UnknownPairAtOrder12(k));
}

// Citation: design LoginThrottle.P9_AddressCapNeedsTenIdentifiers: "The address cap is a sum over rows, each floored at −5, so it is reached only once failures span at least 10 identifiers." (§2.5 line 159)
lemma P6_AddressCapNeedsTenIdentifiers(rows: seq<RepRow>, ip: seq<CodePoint>)
  requires forall i :: 0 <= i < |rows| ==> -5 <= rows[i].score <= 5
  requires AddressAggregate(rows, ip) <= -50
  ensures |set i | 0 <= i < |rows| && rows[i].ip == ip && rows[i].score < 0| >= 10
{
  NegTotalBound(rows, ip);
}

// Citation: "**pair:** the rows for the client address whose identifier is the pending user's username or email have a summed score ≤ −5" (§2.5 line 148)
lemma P7_EmailRowsCount(rows: seq<RepRow>, ip: seq<CodePoint>, u: PendingUser)
  requires u.email.Some? && u.email.value != []
  requires RepRow(ip, u.email.value, -5) in rows && forall r :: r in rows ==> r.score <= 0
  ensures Throttle(rows, ip, u)
{
  assert u.email.value in Identifiers(u);
  PairTotalAtMost(rows, ip, Identifiers(u), RepRow(ip, u.email.value, -5));
}

// Citation: "the rows for the client address …" (§2.5 line 148); "the sum of all negative row scores for the client address" (line 149)
lemma P8_OtherAddressesNeverCount(rows: seq<RepRow>, ip: seq<CodePoint>, u: PendingUser)
  requires forall r :: r in rows ==> r.ip != ip
  ensures !Throttle(rows, ip, u)
{
  NoRowsAtIp(rows, ip, Identifiers(u));
}
