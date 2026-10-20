// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ 19d74cfb95c4ccb8ceca9e939494828f3359c3a5
// Source: §2.3 "Skew" and "Streams" (and §6 line 490), lines 90-97
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/StreamRecheck.dfy
// Round: 1

// ---------------------------------------------------------------- model

datatype Option<T> = None | Some(value: T)

// Times are whole seconds on one shared time line (iat from Authentik's
// clock, revoked_at from Postgres now()), §2.3 line 90.

// §2.3 line 90: "A token is revoked when `iat <= revoked_at + 5 s`."
// None = the cache holds no revocation row for this sub.
predicate IsRevoked(iat: int, revokedAt: Option<int>)
{
  match revokedAt
  case None => false
  case Some(r) => iat <= r + 5
}

// §2.3 line 96: "A suspended or deleted tenant ..."
datatype TenantStatus = Active | Suspended | Deleted

// §2.3 line 96: Unauthenticated / PermissionDenied
datatype StatusCode = Unauthenticated | PermissionDenied

// §2.3 lines 93-95: the state the interceptor re-checks before each message,
// read from the in-memory caches.
datatype StreamState = StreamState(
  primaryIat: int,
  primaryRevokedAt: Option<int>,
  actingIat: int,
  actingRevokedAt: Option<int>,
  tenantStatus: TenantStatus)

datatype Outcome = Write | EndStream(code: StatusCode)

// §2.3 lines 93-96, cases in the spec's order:
//   line 94: revocation of the primary principal's sub and of the acting user's sub
//            -> line 96: "A revoked principal ends the stream with `Unauthenticated`."
//   line 95: the status of the effective tenant
//            -> line 96: "A suspended or deleted tenant ends it with `PermissionDenied`"
//   otherwise the message is written.
function BeforeWrite(s: StreamState): Outcome
{
  if IsRevoked(s.primaryIat, s.primaryRevokedAt) || IsRevoked(s.actingIat, s.actingRevokedAt) then
    EndStream(Unauthenticated)
  else if s.tenantStatus == Suspended || s.tenantStatus == Deleted then
    EndStream(PermissionDenied)
  else
    Write
}

// ---------------------------------------------------------------- properties

// Citation: "A token is revoked when `iat <= revoked_at + 5 s`. … A fresh login within 5 s of a revocation is therefore refused" (§2.3 line 90)
lemma P1_WithinFiveSecondsRevoked(iat: int, revokedAt: int)
  requires revokedAt <= iat <= revokedAt + 5
  ensures IsRevoked(iat, Some(revokedAt))
{
}

// Citation: "`TokenRevocationCacheTests.cs:50-52` moves from +1 s to +6 s, and a +5 s boundary test is added" (§2.3 line 90)
lemma P2_Boundary(revokedAt: int)
  ensures IsRevoked(revokedAt + 5, Some(revokedAt)) && !IsRevoked(revokedAt + 6, Some(revokedAt))
{
}

// Citation: "Before each message it re-checks … the revocation of the primary principal's `sub` and of the acting user's `sub`; the status of the effective tenant. A revoked principal ends the stream with `Unauthenticated`. A suspended or deleted tenant ends it with `PermissionDenied`" (§2.3 lines 93–96)
lemma P3_NoWriteAfterRevocationOrSuspension(s: StreamState)
  requires IsRevoked(s.primaryIat, s.primaryRevokedAt) || IsRevoked(s.actingIat, s.actingRevokedAt) || s.tenantStatus != Active
  ensures !BeforeWrite(s).Write?
{
}
