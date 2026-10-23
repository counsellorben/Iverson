// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T1 S3
// Source: Iverson.Server/Iverson.Api/Grpc/TenantAdminGrpcService.cs RequireUserInTenantAsync, UserNotInTenant, IsCanonicalUserPk (123–141)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/TenantAdminGrpcService.RequireUserInTenantAsync.dfy

// ---------------------------------------------------------------------------
// Shared modelling vocabulary
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

type CsString = s: seq<char> | |s| < 0x8000_0000

newtype int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

// IIdpAdminClient.cs:16-23
datatype IdpUserRecord = IdpUserRecord(Id: CsString, Username: CsString, Email: CsString, Uid: CsString, IsActive: bool, Groups: seq<CsString>, TenantId: Option<CsString>)

// Task<IdpUserRecord?> GetUserAsync: a record, null (404), or a thrown exception.
datatype GetUserResult = Found(record: IdpUserRecord) | Null | Threw

datatype Calls = NoCall | OneGetUserCall

// Outcome of RequireUserInTenantAsync: the returned record, the UserNotInTenant()
// RpcException (PermissionDenied) together with the IdP calls made before it, or an
// exception propagated from GetUserAsync.
datatype GuardResult = Accepted(record: IdpUserRecord) | Refused(calls: Calls) | GuardThrew

// ---------------------------------------------------------------------------
// Spec helpers
// ---------------------------------------------------------------------------

predicate IsDigit(c: char) { '0' <= c <= '9' }

predicate AllDigits(s: seq<char>) { forall i :: 0 <= i < |s| ==> IsDigit(s[i]) }

predicate IsCanonicalPositiveDecimal(s: seq<char>)
{
  |s| > 0 && AllDigits(s) && s[0] != '0'
}

function DigitValue(c: char): int { c as int - '0' as int }

function DecimalValue(s: seq<char>): int
  ensures AllDigits(s) ==> DecimalValue(s) >= 0
  decreases |s|
{
  if |s| == 0 then 0 else DecimalValue(s[..|s| - 1]) * 10 + DigitValue(s[|s| - 1])
}

// ---------------------------------------------------------------------------
// Library boundary
// ---------------------------------------------------------------------------

// int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) (approved axiom).
function IntTryParseNone(s: CsString): Option<int32>
  ensures (|s| > 0 && (forall i :: 0 <= i < |s| ==> '0' <= s[i] <= '9') && DecimalValue(s) <= 0x7FFF_FFFF) ==> IntTryParseNone(s) == Some(DecimalValue(s) as int32)

// The IdP behind IIdpAdminClient (approved axiom).
type Directory

// IIdpAdminClient.GetUserAsync (approved axiom).
function GetUserAsync(idp: Directory, userId: CsString): GetUserResult
  ensures GetUserAsync(idp, userId).Found? ==> GetUserAsync(idp, userId).record.Id == userId

// int.ToString(CultureInfo.InvariantCulture), modelled: '-' then the magnitude's decimal
// digits for negatives, the decimal digits with no leading zero otherwise.
function DigitChar(d: int): char
  requires 0 <= d < 10
{
  ('0' as int + d) as char
}

function NatToString(n: nat): seq<char>
  decreases n
{
  if n < 10 then [DigitChar(n)] else NatToString(n / 10) + [DigitChar(n % 10)]
}

function Pow10(k: nat): nat
{
  if k == 0 then 1 else 10 * Pow10(k - 1)
}

lemma NatToStringLength(n: nat, k: nat)
  requires 1 <= k && n < Pow10(k)
  ensures |NatToString(n)| <= k
  decreases n
{
  if n >= 10 {
    assert Pow10(1) == 10;
    assert k >= 2;
    assert n / 10 < Pow10(k - 1);
    NatToStringLength(n / 10, k - 1);
  }
}

function Int32ToString(x: int32): CsString
{
  assert Pow10(11) == 100_000_000_000;
  var m: nat := if x < 0 then -(x as int) else x as int;
  NatToStringLength(m, 11);
  if x < 0 then ['-'] + NatToString(m) else NatToString(m)
}

// ---------------------------------------------------------------------------
// Transliteration (TenantAdminGrpcService.cs:123-141)
// ---------------------------------------------------------------------------

// :133-134 UserNotInTenant() — the PermissionDenied RpcException; modelled as Refused(calls).

// :138-141
predicate IsCanonicalUserPk(userId: CsString)
{
  var parsed := IntTryParseNone(userId);
  parsed.Some?
  && parsed.value > 0
  && Int32ToString(parsed.value) == userId
}

// :123-131
function RequireUserInTenant(userId: CsString, tenantId: CsString, idp: Directory): GuardResult
{
  if !IsCanonicalUserPk(userId) then
    Refused(NoCall)
  else
    var user := GetUserAsync(idp, userId);
    if user.Threw? then
      GuardThrew
    else if user.Null? || user.record.TenantId != Some(tenantId) then
      Refused(OneGetUserCall)
    else
      Accepted(user.record)
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma NatToStringCanonical(n: nat)
  requires n > 0
  ensures IsCanonicalPositiveDecimal(NatToString(n))
  decreases n
{
  if n >= 10 {
    NatToStringCanonical(n / 10);
    var p := NatToString(n / 10);
    assert NatToString(n) == p + [DigitChar(n % 10)];
    assert NatToString(n)[0] == p[0];
  }
}

lemma CanonicalValuePositive(s: seq<char>)
  requires IsCanonicalPositiveDecimal(s)
  ensures DecimalValue(s) >= 1
  decreases |s|
{
  if |s| == 1 {
    assert s[..0] == [];
  } else {
    var p := s[..|s| - 1];
    assert p[0] == s[0];
    assert IsCanonicalPositiveDecimal(p);
    CanonicalValuePositive(p);
  }
}

lemma CanonicalRoundTrip(s: seq<char>)
  requires IsCanonicalPositiveDecimal(s)
  ensures DecimalValue(s) >= 1
  ensures NatToString(DecimalValue(s)) == s
  decreases |s|
{
  CanonicalValuePositive(s);
  var c := s[|s| - 1];
  var d := DigitValue(c);
  assert IsDigit(c);
  assert DigitChar(d) == c;
  if |s| == 1 {
    assert s[..0] == [];
    assert DecimalValue(s) == d;
    assert s == [c];
  } else {
    var p := s[..|s| - 1];
    assert p[0] == s[0];
    assert IsCanonicalPositiveDecimal(p);
    CanonicalRoundTrip(p);
    var v := DecimalValue(s);
    assert v == DecimalValue(p) * 10 + d;
    assert v >= 10;
    assert v / 10 == DecimalValue(p);
    assert v % 10 == d;
    assert NatToString(v) == NatToString(v / 10) + [DigitChar(v % 10)];
    assert s == p + [c];
  }
}

// Whatever int.TryParse returns, the gate passes only canonical positive decimals.
lemma GateImpliesCanonical(userId: CsString)
  ensures IsCanonicalUserPk(userId) ==> IsCanonicalPositiveDecimal(userId)
{
  var parsed := IntTryParseNone(userId);
  if parsed.Some? && parsed.value > 0 {
    NatToStringCanonical(parsed.value as int);
    assert Int32ToString(parsed.value) == NatToString(parsed.value as int);
  }
}

// ---------------------------------------------------------------------------
// Property lemmas
// ---------------------------------------------------------------------------

// Citation: design TenantUserGuard.P1_NonCanonicalRefusedWithoutCall: "`RequireUserInTenantAsync` first requires `user_id` to be a canonical positive decimal integer; any other value is "not in this tenant" (`PermissionDenied`) with no IdP call." (§2.1 line 42)
lemma P1_NonCanonicalRefusedWithoutCall(userId: CsString, tenantId: CsString, idp: Directory)
  requires !IsCanonicalPositiveDecimal(userId)
  ensures RequireUserInTenant(userId, tenantId, idp) == Refused(NoCall)
{
  GateImpliesCanonical(userId);
}

// Citation: design TenantUserGuard.P2_MismatchRefused: "`GetUserAsync` also requires the returned `pk` to equal the requested pk." (line 42); "uses `GetUserAsync` and compares `tenant_id`" (line 41); "404 maps to "not in this tenant"" (line 40)
lemma P2_MismatchRefused(userId: CsString, tenantId: CsString, idp: Directory)
  requires GetUserAsync(idp, userId).Null? || (GetUserAsync(idp, userId).Found? && GetUserAsync(idp, userId).record.TenantId != Some(tenantId))
  ensures RequireUserInTenant(userId, tenantId, idp).Refused?
{
}

// Citation: design TenantUserGuard.P3_MutationsUseCheckedPk: "RemoveUser and SetTenantAdmin pass the checked record's pk, not the raw request string, to `DeactivateUserAsync`, `AddGroupAsync` and `RemoveGroupAsync`." (line 42)
lemma P3_MutationsUseCheckedPk(userId: CsString, tenantId: CsString, idp: Directory)
  requires RequireUserInTenant(userId, tenantId, idp).Accepted?
  ensures RequireUserInTenant(userId, tenantId, idp).record.Id == userId
       && RequireUserInTenant(userId, tenantId, idp).record.TenantId == Some(tenantId)
{
}

// Citation: "`RequireUserInTenantAsync` (RemoveUser, SetTenantAdmin) uses `GetUserAsync` and compares `tenant_id`" (§2.1 line 41); test `RemoveUser_ReadsTheTargetByPk_AndNeverListsTheDirectory` (T1 S1 1f), generalised
lemma P4_CanonicalPkReachesTheIdp(userId: CsString, tenantId: CsString, idp: Directory)
  requires IsCanonicalPositiveDecimal(userId) && DecimalValue(userId) <= 0x7FFF_FFFF
  ensures RequireUserInTenant(userId, tenantId, idp) != Refused(NoCall)
{
  CanonicalRoundTrip(userId);
  var v := DecimalValue(userId);
  assert IntTryParseNone(userId) == Some(v as int32);
  assert Int32ToString(v as int32) == NatToString(v);
  assert IsCanonicalUserPk(userId);
}
