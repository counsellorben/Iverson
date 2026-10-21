// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a11e848d722d05cc808840ae796e6afdb2101aa2
// Source: §2.1 "IdP client", lines 40-42
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/TenantUserGuard.dfy
// Round: 3

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// Line 40: GetUserAsync "returns `uid`, `username`, `email`, `is_active`,
// `groups` and `attributes.tenant_id`"; line 42: "the returned `pk`".
// pk is held as its decimal rendering so it compares with the request string.
datatype UserRecord = UserRecord(
  pk: string,
  uid: string,
  username: string,
  email: string,
  isActive: bool,
  groups: seq<string>,
  tenantId: string)

// The IdP as seen through `GET /api/v3/core/users/{pk}/` (line 40): the key is
// the path segment requested; the value is the user object the IdP returns
// for it (whose own pk need not equal the key). A missing key is a 404.
type Directory = map<string, UserRecord>

// What GetUserAsync's HTTP call yields (line 40): a user object or 404.
datatype GetUserResponse =
  | Found(pk: string, uid: string, username: string, email: string,
          isActive: bool, groups: seq<string>, tenantId: string)
  | NotFound

// Whether the guard made an IdP call (line 42: "with no IdP call").
datatype IdpCalls = NoCall | OneGetUserCall

// Outcome of RequireUserInTenantAsync: the checked record, or
// "not in this tenant" (`PermissionDenied`, line 42).
datatype GuardResult =
  | Accepted(record: UserRecord)
  | Refused(calls: IdpCalls)

predicate IsDigit(c: char)
{
  '0' <= c <= '9'
}

// Line 42: "a canonical positive decimal integer": non-empty, ASCII digits
// only, no leading zero (canonical), and not zero (positive; with no leading
// zero the only zero-valued string is "0", excluded by the first-char rule).
predicate IsCanonicalPositiveDecimal(s: string)
{
  |s| > 0
  && (forall i :: 0 <= i < |s| ==> IsDigit(s[i]))
  && s[0] != '0'
}

// Line 40: `GET /api/v3/core/users/{pk}/`; 404 when the IdP has no such user.
function LookupResult(idp: Directory, pk: string): GetUserResponse
{
  if pk in idp then
    var u := idp[pk];
    Found(u.pk, u.uid, u.username, u.email, u.isActive, u.groups, u.tenantId)
  else
    NotFound
}

function ToRecord(r: GetUserResponse): UserRecord
  requires r.Found?
{
  UserRecord(r.pk, r.uid, r.username, r.email, r.isActive, r.groups, r.tenantId)
}

// GetUserAsync (lines 40, 42): calls the endpoint; 404 maps to "not in this
// tenant" (None); "also requires the returned `pk` to equal the requested pk"
// (a mismatch is likewise not a usable record: None).
datatype Option<T> = None | Some(value: T)

function GetUser(idp: Directory, pk: string): Option<UserRecord>
{
  var r := LookupResult(idp, pk);
  if r.NotFound? then None                       // line 40: 404
  else if r.pk != pk then None                   // line 42: pk must match
  else Some(ToRecord(r))
}

// RequireUserInTenantAsync (lines 41-42), cases in the spec's order:
//   1. user_id not canonical positive decimal -> refused, no IdP call (l.42)
//   2. GetUserAsync (l.41) -> 404 / pk mismatch -> refused (l.40, l.42)
//   3. compare tenant_id (l.41) -> mismatch refused, else the checked record
function RequireUserInTenant(userId: string, tenant: string, idp: Directory): GuardResult
{
  if !IsCanonicalPositiveDecimal(userId) then
    Refused(NoCall)
  else
    var got := GetUser(idp, userId);
    if got.None? then
      Refused(OneGetUserCall)
    else if got.value.tenantId != tenant then
      Refused(OneGetUserCall)
    else
      Accepted(got.value)
}

// Line 42: RemoveUser and SetTenantAdmin pass the checked record's pk to
// DeactivateUserAsync, AddGroupAsync and RemoveGroupAsync.
function MutationTargetPk(r: GuardResult): string
  requires r.Accepted?
{
  r.record.pk
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "`RequireUserInTenantAsync` first requires `user_id` to be a canonical positive decimal integer; any other value is "not in this tenant" (`PermissionDenied`) with no IdP call." (§2.1 line 42)
lemma P1_NonCanonicalRefusedWithoutCall(userId: string, tenant: string, idp: Directory)
  requires !IsCanonicalPositiveDecimal(userId)
  ensures RequireUserInTenant(userId, tenant, idp) == Refused(NoCall)
{
}

// Citation: "`GetUserAsync` also requires the returned `pk` to equal the requested pk." (line 42); "uses `GetUserAsync` and compares `tenant_id`" (line 41); "404 maps to "not in this tenant"" (line 40)
lemma P2_MismatchRefused(userId: string, tenant: string, idp: Directory)
  requires IsCanonicalPositiveDecimal(userId)
  requires LookupResult(idp, userId).Found? ==> (LookupResult(idp, userId).pk != userId || LookupResult(idp, userId).tenantId != tenant)
  ensures RequireUserInTenant(userId, tenant, idp).Refused?
{
}

// Citation: "RemoveUser and SetTenantAdmin pass the checked record's pk, not the raw request string, to `DeactivateUserAsync`, `AddGroupAsync` and `RemoveGroupAsync`." (line 42)
lemma P3_MutationsUseCheckedPk(userId: string, tenant: string, idp: Directory)
  requires RequireUserInTenant(userId, tenant, idp).Accepted?
  ensures RequireUserInTenant(userId, tenant, idp).record.pk == userId
       && RequireUserInTenant(userId, tenant, idp).record.tenantId == tenant
{
}
