// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a11e848d722d05cc808840ae796e6afdb2101aa2
// Source: §2.1 "Invite quota" (and §6 line 490), lines 49-55
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/InviteQuota.dfy
// Round: 3

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// Line 50: "limited to **100 per tenant per UTC day**"; line 51's SQL hard-codes
// `WHERE "IversonInviteQuota"."Count" < 100`.
const Limit: int := 100

// Line 51: table "IversonInviteQuota" ("TenantId", "Day", "Count"),
// PRIMARY KEY ("TenantId", "Day"). The model is the one row for one
// (TenantId, Day) key: either absent, or present with its "Count".
datatype QuotaRow = NoRow | Row(count: int)

// Result of the reservation statement: the new state and whether
// `RETURNING "Count"` returned a row.
datatype ReserveResult = ReserveResult(next: QuotaRow, returned: bool)

// Line 51: `INSERT … VALUES (@TenantId, @Day, 1)
//   ON CONFLICT ("TenantId", "Day") DO UPDATE SET "Count" = "IversonInviteQuota"."Count" + 1
//   WHERE "IversonInviteQuota"."Count" < 100 RETURNING "Count"`.
// Cases in the statement's order:
//   1. no conflicting row -> insert with Count = 1, row returned;
//   2. conflict and Count < 100 -> Count + 1, row returned;
//   3. conflict and the WHERE is false -> no update, no row returned.
function Reserve(s: QuotaRow): ReserveResult
{
  match s
  case NoRow => ReserveResult(Row(1), true)
  case Row(c) =>
    if c < Limit then ReserveResult(Row(c + 1), true)
    else ReserveResult(s, false)
}

// Line 51: "No row returned means over quota: `ResourceExhausted`".
// ReturnsRow(s): the next reservation against state s returns a row
// (the invite proceeds to Authentik).
predicate ReturnsRow(s: QuotaRow)
{
  Reserve(s).returned
}

// Line 51: "The api reserves a slot before calling Authentik ... A reserved
// slot is not returned if the IdP call then fails." The IdP outcome is taken
// as an input and deliberately has no effect on the table: there is no
// decrement path.
function InviteAttempt(s: QuotaRow, idpFails: bool): QuotaRow
{
  Reserve(s).next
}

// The stored "Count" (absent row reads as 0).
function StoredCount(s: QuotaRow): int
{
  match s
  case NoRow => 0
  case Row(c) => c
}

// State of the (TenantId, Day) row after `attempts` InviteUser calls on
// that tenant's day, starting from no row (line 55: rows are one per
// tenant per active day; a new day is a new key, so it starts absent).
// §6 line 490: attempts from two api instances sharing Postgres; the
// single upsert statement is applied atomically per row, so concurrent
// attempts are modelled as one serial sequence. The IdP outcome does not
// matter (InviteAttempt ignores it), so `false` is passed.
function CountAfter(attempts: nat): QuotaRow
{
  if attempts == 0 then NoRow
  else InviteAttempt(CountAfter(attempts - 1), false)
}

// Number of the first `attempts` InviteUser calls whose reservation
// returned a row, i.e. invites that were admitted under the quota.
function ReservedCount(attempts: nat): nat
{
  if attempts == 0 then 0
  else ReservedCount(attempts - 1) + (if ReturnsRow(CountAfter(attempts - 1)) then 1 else 0)
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma IdpOutcomeIrrelevant(s: QuotaRow)
  ensures InviteAttempt(s, true) == InviteAttempt(s, false)
{
}

lemma CountAfterInvariant(n: nat)
  ensures CountAfter(n).Row? || n == 0
  ensures 0 <= StoredCount(CountAfter(n)) <= Limit
  ensures StoredCount(CountAfter(n)) == ReservedCount(n)
  ensures n <= Limit ==> StoredCount(CountAfter(n)) == n
  ensures n >= Limit ==> StoredCount(CountAfter(n)) == Limit
{
  if n > 0 {
    CountAfterInvariant(n - 1);
  }
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "`InviteUser` is limited to **100 per tenant per UTC day**." (§2.1 line 50)
lemma P1_AtMostHundredReserved(attempts: nat)
  ensures ReservedCount(attempts) <= 100
{
  CountAfterInvariant(attempts);
}

// Citation: "the 101st invite of a tenant's day is refused across two api instances sharing Postgres" (§6 line 490)
lemma P2_HundredFirstRefused(attempts: nat)
  requires attempts >= 100
  ensures !ReturnsRow(CountAfter(attempts))
{
  CountAfterInvariant(attempts);
}

// Citation: "A reserved slot is not returned if the IdP call then fails." (§2.1 line 51)
lemma P3_CountMonotone(a: nat, b: nat)
  requires a <= b
  ensures StoredCount(CountAfter(a)) <= StoredCount(CountAfter(b))
{
  CountAfterInvariant(a);
  CountAfterInvariant(b);
}
