// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T5 S19
// Source: Iverson.Server/Iverson.Api/Grpc/StreamRevalidationInterceptor.cs RecheckAsync (45–55), RevalidatingServerStreamWriter.WriteAsync ×2 (75–85); with Tenancy/TokenRevocationCheck.cs IsRevokedAsync (13–24)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/StreamRevalidationInterceptor.RecheckAsync.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// C# string: UTF-16 code units under --unicode-char false.
type CsString = s: seq<char> | |s| < 0x8000_0000

// DateTimeOffset as its UtcTicks (as in TokenRevocationCache.IsRevokedAsync).
const MaxTicks := 3155378975999999999
newtype Ticks = t: int | 0 <= t <= 3155378975999999999
const SecondTicks := 10_000_000

// A claim and a principal (as in OperatorAuthorizationPolicy.IsSatisfiedBy).
datatype Claim = Claim(ctype: CsString, value: CsString)

// ClaimsPrincipal.FindFirst(type)?.Value: the first claim of that type.
function FindFirst(p: seq<Claim>, t: CsString): Option<CsString>
{
  if |p| == 0 then None
  else if p[0].ctype == t then Some(p[0].value)
  else FindFirst(p[1..], t)
}

// ---------------------------------------------------------------------------
// Approved axioms (library boundary)
// ---------------------------------------------------------------------------

type CacheEnv

type Msg

function CacheRevoked(env: CacheEnv, sub: CsString, iat: Option<Ticks>): bool

function TenantStatus(env: CacheEnv, tenantId: CsString): Option<CsString>

function LongTryParse(s: Option<CsString>): Option<int>
  ensures s.None? ==> LongTryParse(s).None?
  ensures LongTryParse(s).Some? ==> -0x8000_0000_0000_0000 <= LongTryParse(s).value < 0x8000_0000_0000_0000

// ---------------------------------------------------------------------------
// Modelled library: DateTimeOffset.FromUnixTimeSeconds(long)
//   if (seconds < UnixMinSeconds || seconds > UnixMaxSeconds) throw ArgumentOutOfRangeException;
//   ticks = seconds * TicksPerSecond + UnixEpochTicks
// ---------------------------------------------------------------------------

const UnixEpochTicks := 621_355_968_000_000_000
const UnixMinSeconds := -62_135_596_800    // DateTime.MinTicks / TicksPerSecond - UnixEpochSeconds
const UnixMaxSeconds := 253_402_300_799    // DateTime.MaxTicks / TicksPerSecond - UnixEpochSeconds

datatype DtoResult = Dto(ticks: Ticks) | DtoThrew

function FromUnixTimeSeconds(seconds: int): DtoResult
  requires -0x8000_0000_0000_0000 <= seconds < 0x8000_0000_0000_0000   // long parameter
{
  if seconds < UnixMinSeconds || seconds > UnixMaxSeconds then DtoThrew
  else Dto((seconds * SecondTicks + UnixEpochTicks) as Ticks)
}

// ---------------------------------------------------------------------------
// Transliteration: Tenancy/TokenRevocationCheck.cs IsRevokedAsync (13–24)
// ---------------------------------------------------------------------------

datatype CheckResult = Checked(revoked: bool) | CheckThrew

function IsRevokedAsync(env: CacheEnv, principal: seq<Claim>): CheckResult
{
  var sub := FindFirst(principal, "sub");                          // 16
  if sub.None? then Checked(false)                                  // 17-18
  else
    var parsed := LongTryParse(FindFirst(principal, "iat"));       // 20
    if parsed.Some? then
      var d := FromUnixTimeSeconds(parsed.value);                   // 21
      if d.DtoThrew? then CheckThrew
      else Checked(CacheRevoked(env, sub.value, Some(d.ticks)))     // 23
    else
      Checked(CacheRevoked(env, sub.value, None))                   // 22, 23
}

// ---------------------------------------------------------------------------
// Transliteration: Grpc/StreamRevalidationInterceptor.cs RecheckAsync (45–55)
// ---------------------------------------------------------------------------

datatype StreamCall = StreamCall(primary: seq<Claim>, acting: Option<seq<Claim>>)

datatype RecheckOutcome = Write | EndUnauthenticated | EndPermissionDenied(detail: seq<char>) | RecheckThrew

function Recheck(env: CacheEnv, c: StreamCall): RecheckOutcome
{
  // 47: IsRevokedAsync(primary)
  var p := IsRevokedAsync(env, c.primary);
  if p.CheckThrew? then RecheckThrew
  else if p.revoked then EndUnauthenticated                         // 49
  else
    // 48: actingUser is not null && IsRevokedAsync(actingUser)
    var a := if c.acting.Some? then IsRevokedAsync(env, c.acting.value) else Checked(false);
    if a.CheckThrew? then RecheckThrew
    else if a.revoked then EndUnauthenticated                       // 49
    else
      // 51: actingUser?.FindFirst("tenant_id")?.Value
      var tenantId := if c.acting.Some? then FindFirst(c.acting.value, "tenant_id") else None;
      // 52-53: tenantId is not null && GetStatusAsync(tenantId) is null or "suspended" or "deleted"
      if tenantId.Some?
         && (var st := TenantStatus(env, tenantId.value);
             st.None? || st.value == "suspended" || st.value == "deleted")
      then EndPermissionDenied("Tenant '" + tenantId.value + "' is not active.")   // 54
      else Write                                                    // 55
}

// ---------------------------------------------------------------------------
// Transliteration: RevalidatingServerStreamWriter.WriteAsync x2 (75–85)
// recheck = ct => RecheckAsync(primary, actingUser, ct) (line 41); inner.WriteAsync appends m.
// Returns (outcome, written').
// ---------------------------------------------------------------------------

function WriteThrough(env: CacheEnv, c: StreamCall, m: Msg, written: seq<Msg>, twoArgumentForm: bool): (RecheckOutcome, seq<Msg>)
{
  if twoArgumentForm then
    // 81-85: await recheck(cancellationToken); await inner.WriteAsync(message, cancellationToken);
    var r := Recheck(env, c);
    if !r.Write? then (r, written)
    else (Write, written + [m])
  else
    // 75-79: await recheck(callCancellation); await inner.WriteAsync(message);
    var r := Recheck(env, c);
    if !r.Write? then (r, written)
    else (Write, written + [m])
}

// ---------------------------------------------------------------------------
// Property vocabulary
// ---------------------------------------------------------------------------

// p's iat claim is absent, does not parse, or parses to seconds inside FromUnixTimeSeconds's range.
predicate IatReadable(p: seq<Claim>)
{
  var parsed := LongTryParse(FindFirst(p, "iat"));
  parsed.None? || (UnixMinSeconds <= parsed.value <= UnixMaxSeconds)
}

// The iat TokenRevocationCheck reads (None also when it would throw; only meaningful under IatReadable).
function ReadIat(p: seq<Claim>): Option<Ticks>
{
  var parsed := LongTryParse(FindFirst(p, "iat"));
  if parsed.Some? then
    var d := FromUnixTimeSeconds(parsed.value);
    if d.Dto? then Some(d.ticks) else None
  else None
}

predicate PrincipalRevoked(env: CacheEnv, p: seq<Claim>)
{
  FindFirst(p, "sub").Some? && CacheRevoked(env, FindFirst(p, "sub").value, ReadIat(p))
}

predicate IsActiveStatus(s: Option<CsString>)
{
  s.Some? && s.value != "suspended" && s.value != "deleted"
}

// Helper: a check that does not throw answers PrincipalRevoked; under IatReadable it does not throw.
lemma CheckAgreesWithRevoked(env: CacheEnv, p: seq<Claim>)
  ensures IsRevokedAsync(env, p).Checked? ==> IsRevokedAsync(env, p).revoked == PrincipalRevoked(env, p)
  ensures IatReadable(p) ==> IsRevokedAsync(env, p).Checked?
{
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: design StreamRecheck.P3_NoWriteAfterRevocationOrSuspension: "Before each message it re-checks … the revocation of the primary principal's `sub` and of the acting user's `sub`; the status of the effective tenant. A revoked principal ends the stream with `Unauthenticated`. A suspended or deleted tenant ends it with `PermissionDenied`" (§2.3 lines 93–96)
lemma P1_NoWriteAfterRevocationOrSuspension(env: CacheEnv, c: StreamCall)
  requires PrincipalRevoked(env, c.primary)
        || (c.acting.Some? && PrincipalRevoked(env, c.acting.value))
        || (c.acting.Some? && FindFirst(c.acting.value, "tenant_id").Some?
            && !IsActiveStatus(TenantStatus(env, FindFirst(c.acting.value, "tenant_id").value)))
  ensures !Recheck(env, c).Write?
{
  CheckAgreesWithRevoked(env, c.primary);
  if c.acting.Some? {
    CheckAgreesWithRevoked(env, c.acting.value);
  }
}

// Citation: "A revoked principal ends the stream with `Unauthenticated`." (§2.3 line 96); test `ARevokedPrincipal_EndsTheStreamUnauthenticated_AtItsNextMessage`
lemma P2_RevokedEndsUnauthenticated(env: CacheEnv, c: StreamCall)
  requires IatReadable(c.primary) && (c.acting.Some? ==> IatReadable(c.acting.value))
  requires PrincipalRevoked(env, c.primary) || (c.acting.Some? && PrincipalRevoked(env, c.acting.value))
  ensures Recheck(env, c) == EndUnauthenticated
{
  CheckAgreesWithRevoked(env, c.primary);
  if c.acting.Some? {
    CheckAgreesWithRevoked(env, c.acting.value);
  }
}

// Citation: "A suspended or deleted tenant ends it with `PermissionDenied` "Tenant '…' is not active.", as the unary paths do." (§2.3 line 96); test `ATenantNoLongerActive_EndsTheStreamPermissionDenied_AtItsNextMessage`
lemma P3_InactiveTenantEndsPermissionDenied(env: CacheEnv, c: StreamCall, t: CsString)
  requires IatReadable(c.primary) && c.acting.Some? && IatReadable(c.acting.value)
  requires !PrincipalRevoked(env, c.primary) && !PrincipalRevoked(env, c.acting.value)
  requires FindFirst(c.acting.value, "tenant_id") == Some(t) && !IsActiveStatus(TenantStatus(env, t))
  ensures Recheck(env, c) == EndPermissionDenied("Tenant '" + t + "' is not active.")
{
  CheckAgreesWithRevoked(env, c.primary);
  CheckAgreesWithRevoked(env, c.acting.value);
}

// Citation: "The wrapper implements both `WriteAsync(T)` and `WriteAsync(T, CancellationToken)` and passes `WriteOptions` through" (§2.3 line 97); tests `ARevokedPrincipal_EndsTheStreamUnauthenticated_AtItsNextMessage` (both forms) and `NothingChanged_BothWriteFormsWriteThrough_PassingTheTokenAndTheWriteOptions`
lemma P4_BothWriteFormsRecheckFirst(env: CacheEnv, c: StreamCall, m: Msg, written: seq<Msg>, twoArgumentForm: bool)
  ensures WriteThrough(env, c, m, written, twoArgumentForm).1 == (if Recheck(env, c).Write? then written + [m] else written)
{
}
