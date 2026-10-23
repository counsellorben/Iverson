// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T5 S3
// Source: Iverson.Server/Iverson.Api/Tenancy/TokenRevocationCache.cs IsRevokedAsync (35–42)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/TokenRevocationCache.IsRevokedAsync.dfy

// ---- Shared vocabulary ----
datatype Option<T> = None | Some(value: T)
type CsString = s: seq<char> | |s| < 0x8000_0000

// ---- Library boundary: DateTimeOffset as UTC ticks ----
const MaxTicks := 3155378975999999999  // DateTimeOffset.MaxValue.UtcTicks
newtype Ticks = t: int | 0 <= t <= 3155378975999999999
const SecondTicks := 10_000_000

// DateTimeOffset + TimeSpan: past MaxValue throws ArgumentOutOfRangeException.
datatype AddResult = AddOk(v: Ticks) | AddThrew
function AddTimeSpan(d: Ticks, spanTicks: int): AddResult
  requires spanTicks >= 0
{
  if d as int + spanTicks <= MaxTicks then AddOk((d as int + spanTicks) as Ticks) else AddThrew
}

// private static readonly TimeSpan Skew = TimeSpan.FromSeconds(5);   (line 26)
const SkewTicks := 5 * SecondTicks

// private sealed record Snapshot(IReadOnlyDictionary<string, DateTimeOffset> RevokedAt, ...)  (line 33)
// LoadedAt is not read by IsRevokedAsync and is omitted.
datatype Snapshot = Snapshot(RevokedAt: map<CsString, Ticks>)

datatype RevokedResult = Revoked(b: bool) | RevokedThrew

// IsRevokedAsync (lines 35–42); s is the snapshot CurrentSnapshotAsync returns (line 37).
function IsRevoked(s: Snapshot, sub: CsString, issuedAt: Option<Ticks>): RevokedResult
{
  // line 40: snapshot.RevokedAt.TryGetValue(sub, out var revokedAt)
  if sub !in s.RevokedAt then Revoked(false)
  else
    var revokedAt := s.RevokedAt[sub];
    // line 41: issuedAt is null || ...
    if issuedAt.None? then Revoked(true)
    else
      // line 41: issuedAt <= revokedAt + Skew  (right operand evaluated; may throw)
      match AddTimeSpan(revokedAt, SkewTicks)
      case AddThrew => RevokedThrew
      case AddOk(limit) => Revoked(issuedAt.value <= limit)
}

// ---- Properties ----

// Citation: design StreamRecheck.P1_WithinFiveSecondsRevoked: "A token is revoked when `iat <= revoked_at + 5 s`. … A fresh login within 5 s of a revocation is therefore refused" (§2.3 line 90)
lemma P1_WithinFiveSecondsRevoked(s: Snapshot, sub: CsString, iat: int)
  requires sub in s.RevokedAt
  requires s.RevokedAt[sub] as int <= iat <= s.RevokedAt[sub] as int + 5 * SecondTicks <= MaxTicks
  ensures IsRevoked(s, sub, Some(iat as Ticks)) == Revoked(true)
{
}

// Citation: design StreamRecheck.P2_Boundary: "`TokenRevocationCacheTests.cs:50-52` moves from +1 s to +6 s, and a +5 s boundary test is added" (§2.3 line 90)
lemma P2_Boundary(s: Snapshot, sub: CsString)
  requires sub in s.RevokedAt && s.RevokedAt[sub] as int + 6 * SecondTicks <= MaxTicks
  ensures IsRevoked(s, sub, Some((s.RevokedAt[sub] as int + 5 * SecondTicks) as Ticks)) == Revoked(true)
       && IsRevoked(s, sub, Some((s.RevokedAt[sub] as int + 6 * SecondTicks) as Ticks)) == Revoked(false)
{
}

// Citation: "A token is revoked when `iat <= revoked_at + 5 s`." (§2.3 line 90)
lemma P3_NoRowNotRevoked(s: Snapshot, sub: CsString, issuedAt: Option<Ticks>)
  requires sub !in s.RevokedAt
  ensures IsRevoked(s, sub, issuedAt) == Revoked(false)
{
}
