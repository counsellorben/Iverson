// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T3 S3
// Source: Iverson.Server/Iverson.Sql/InviteQuotaRepository.cs TryReserveAsync (27–37)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/InviteQuotaRepository.TryReserveAsync.dfy

// ---------------------------------------------------------------- vocabulary

datatype Option<T> = None | Some(value: T)

// PostgreSQL `int` (int4): the "Count" column's type.
newtype int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

// ---------------------------------------------------------------- state

// The one row of one ("TenantId", "Day") key of "IversonInviteQuota".
datatype QuotaRow = NoRow | Row(count: int32)

datatype ReserveResult = ReserveResult(returned: bool, next: QuotaRow)

// Outcome of the SQL statement: either it ran (the RETURNING rows, at most one
// for a single-key upsert, and the new row) or it raised (int4 overflow).
datatype StatementOutcome =
  | Ran(returning: Option<int32>, after: QuotaRow)
  | Raised

// ---------------------------------------------------------------- library: PostgreSQL

// INSERT INTO "IversonInviteQuota" ("TenantId", "Day", "Count")
// VALUES (@TenantId, @Day, 1)
// ON CONFLICT ("TenantId", "Day") DO UPDATE
//     SET "Count" = "IversonInviteQuota"."Count" + 1
//     WHERE "IversonInviteQuota"."Count" < 100
// RETURNING "Count"
function UpsertStatement(s: QuotaRow): StatementOutcome
{
  match s
  case NoRow =>
    // No conflict: the row is inserted with Count = 1 and returned.
    Ran(Some(1), Row(1))
  case Row(c) =>
    // Conflict: DO UPDATE runs only where its WHERE holds.
    if c < 100 then
      // int4 + int4 raises on overflow ("integer out of range").
      if (c as int) + 1 >= 0x8000_0000 then Raised
      else Ran(Some((c as int + 1) as int32), Row((c as int + 1) as int32))
    else
      // WHERE false: the row is neither updated nor returned.
      Ran(None, s)
}

// ---------------------------------------------------------------- library: Dapper

// QuerySingleOrDefaultAsync<int?>: no row -> null (default), one row -> its value.
function QuerySingleOrDefault(rows: Option<int32>): Option<int32>
{
  rows
}

// ---------------------------------------------------------------- transliteration

// public async Task<bool> TryReserveAsync(string tenantId, DateOnly day) =>
//     await sql.QuerySingleOrDefaultAsync<int?>(<statement>, new { TenantId, Day }) is not null;
// The model is per key: `s` is the row for (tenantId, day) before the call.
// A thrown statement leaves the row unchanged (the statement's transaction aborts)
// and the call returns no `true`; it is folded into `returned == false`.
function Reserve(s: QuotaRow): ReserveResult
{
  match UpsertStatement(s)
  case Raised => ReserveResult(false, s)
  case Ran(rows, after) => ReserveResult(QuerySingleOrDefault(rows).Some?, after)
}

// ---------------------------------------------------------------- harness

// `n` reservations applied serially from no row (the row lock taken by
// ON CONFLICT DO UPDATE serialises concurrent statements on one key).
function CountAfter(n: nat): QuotaRow
{
  if n == 0 then NoRow else Reserve(CountAfter(n - 1)).next
}

// How many of those `n` reservations returned a row.
function ReservedCount(n: nat): nat
{
  if n == 0 then 0
  else ReservedCount(n - 1) + (if Reserve(CountAfter(n - 1)).returned then 1 else 0)
}

function StoredCount(s: QuotaRow): int
{
  match s
  case NoRow => 0
  case Row(c) => c as int
}

function Min(a: int, b: int): int { if a <= b then a else b }

// ---------------------------------------------------------------- helper lemmas

// The overflow path is dead from every row state: the WHERE guard (c < 100)
// bounds c + 1 by 100, so the Raised fold in Reserve never masks an outcome.
lemma StatementNeverRaises(s: QuotaRow)
  ensures UpsertStatement(s) != Raised
{
}

lemma CountAfterShape(n: nat)
  ensures n == 0 ==> CountAfter(n) == NoRow
  ensures n > 0 ==> CountAfter(n) == Row(Min(n, 100) as int32)
  decreases n
{
  if n > 0 {
    CountAfterShape(n - 1);
  }
}

lemma ReservedCountIsMin(n: nat)
  ensures ReservedCount(n) == Min(n, 100)
  decreases n
{
  if n > 0 {
    ReservedCountIsMin(n - 1);
    CountAfterShape(n - 1);
  }
}

// ---------------------------------------------------------------- properties

// Citation: design InviteQuota.P1_AtMostHundredReserved: "`InviteUser` is limited to **100 per tenant per UTC day**." (§2.1 line 50)
lemma P1_AtMostHundredReserved(attempts: nat)
  ensures ReservedCount(attempts) <= 100
{
  ReservedCountIsMin(attempts);
}

// Citation: design InviteQuota.P2_HundredFirstRefused: "the 101st invite of a tenant's day is refused across two api instances sharing Postgres" (§6 line 490)
lemma P2_HundredFirstRefused(attempts: nat)
  requires attempts >= 100
  ensures !Reserve(CountAfter(attempts)).returned
{
  CountAfterShape(attempts);
}

// Citation: design InviteQuota.P3_CountMonotone: "A reserved slot is not returned if the IdP call then fails." (§2.1 line 51)
lemma P3_CountMonotone(a: nat, b: nat)
  requires a <= b
  ensures StoredCount(CountAfter(a)) <= StoredCount(CountAfter(b))
{
  CountAfterShape(a);
  CountAfterShape(b);
}
