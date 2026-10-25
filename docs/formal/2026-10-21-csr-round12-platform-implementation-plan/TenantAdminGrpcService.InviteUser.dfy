// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T3 S3
// Source: Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs InviteUser (29–53), RequireActiveTenantAsync (106–114)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/TenantAdminGrpcService.InviteUser.dfy

// ---- Shared vocabulary ----
datatype Option<T> = None | Some(value: T)
type CsString = s: seq<char> | |s| < 0x8000_0000

// ---- Approved axioms (library boundary) ----
type Env

type Date(==)

function TenantStatus(env: Env, tenantId: CsString): Option<CsString>

function TryReserve(env: Env, tenantId: CsString, day: Date): bool

function UtcToday(env: Env): Date

function CreateUserThrows(env: Env): bool

// ---- Model ----
datatype InviteCall = InviteCall(tenantClaim: Option<CsString>)
datatype Effect = Reserve(tenant: CsString, day: Date) | CreateUser(tenant: CsString)
datatype InviteStatus = Ok | PermissionDenied | ResourceExhausted | InviteThrew
datatype InviteResult = InviteResult(status: InviteStatus, effects: seq<Effect>)

const Suspended: CsString := "suspended"
const Deleted: CsString := "deleted"

predicate IsActive(s: Option<CsString>)
{
  s.Some? && s.value != Suspended && s.value != Deleted
}

// RequireActiveTenantAsync (106–114): None = threw PermissionDenied, Some(tenantId) = returned.
function RequireActiveTenant(env: Env, c: InviteCall): Option<CsString>
{
  if c.tenantClaim.None? then None                                       // 108–109 ?? throw PermissionDenied
  else
    var tenantId := c.tenantClaim.value;
    var status := TenantStatus(env, tenantId);                           // 110
    if status.None? || status.value == Suspended || status.value == Deleted then None // 111–112
    else Some(tenantId)                                                  // 113
}

// InviteUser (29–53)
function InviteUser(env: Env, c: InviteCall): InviteResult
{
  var t := RequireActiveTenant(env, c);                                  // 31
  if t.None? then InviteResult(PermissionDenied, [])                     // 31 (exception propagates)
  else
    var tenantId := t.value;
    var today := UtcToday(env);                                          // 34 DateOnly.FromDateTime(DateTime.UtcNow)
    var reserve := Reserve(tenantId, today);                             // 34 TryReserveAsync
    if !TryReserve(env, tenantId, today) then
      InviteResult(ResourceExhausted, [reserve])                         // 35
    else if CreateUserThrows(env) then
      InviteResult(InviteThrew, [reserve, CreateUser(tenantId)])         // 40–44 CreateUserAsync throws
    else
      InviteResult(Ok, [reserve, CreateUser(tenantId)])                  // 40–52 (audit ignored)
}

// ---- Properties ----

// Citation: "No row returned means over quota: `ResourceExhausted` ("the tenant's daily invitation limit is reached")." (§2.1 line 51); test `InviteUser_DailyLimitReached_ThrowsResourceExhaustedAndDoesNotCreateUser`
lemma P1_LimitReachedRefusesWithoutCallingTheIdp(env: Env, c: InviteCall)
  requires c.tenantClaim.Some? && IsActive(TenantStatus(env, c.tenantClaim.value))
  requires !TryReserve(env, c.tenantClaim.value, UtcToday(env))
  ensures InviteUser(env, c).status == ResourceExhausted
       && forall e :: e in InviteUser(env, c).effects ==> !e.CreateUser?
{
}

// Citation: "The api reserves a slot before calling Authentik" (§2.1 line 51); test `InviteUser_ReservesTodaysSlotForTheCallersTenant_BeforeCallingTheIdp`
lemma P2_ReservesTheCallersTenantTodayFirst(env: Env, c: InviteCall, i: nat)
  requires c.tenantClaim.Some?
  requires i < |InviteUser(env, c).effects| && InviteUser(env, c).effects[i].CreateUser?
  ensures exists j :: 0 <= j < i && InviteUser(env, c).effects[j] == Reserve(c.tenantClaim.value, UtcToday(env))
{
  assert InviteUser(env, c).effects[0] == Reserve(c.tenantClaim.value, UtcToday(env));
}

// Citation: tests `InviteUser_SuspendedTenant_ThrowsPermissionDeniedAndDoesNotCreateUser` and `InviteUser_NoTenantIdClaim_ThrowsPermissionDeniedAndDoesNotCreateUser`, generalised
lemma P3_InactiveTenantReservesAndCreatesNothing(env: Env, c: InviteCall)
  requires c.tenantClaim.None? || !IsActive(TenantStatus(env, c.tenantClaim.value))
  ensures InviteUser(env, c).status == PermissionDenied && InviteUser(env, c).effects == []
{
}
