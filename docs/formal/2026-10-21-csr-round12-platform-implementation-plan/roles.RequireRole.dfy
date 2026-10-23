// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T6 S4
// Source: Iverson.AdminUI/src/auth/roles.tsx RolesProvider state (40–52), hasRole (55–61), RequireRole (74–112)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/roles.RequireRole.dfy

// ---- Shared vocabulary ----------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// TypeScript strings: UTF-16 code units under --unicode-char false.
type CsString = s: seq<char> | |s| < 0x8000_0000

// ---- Library boundary (approved axiom) ------------------------------------------------------------

// ApiFailure (../api/client): opaque; only carried through.
type Failure(==)

// ---- Model types ----------------------------------------------------------------------------------

// ConsoleRolesResponse (api/types.ts:160-163)
datatype RolesResponse = RolesResponse(operator: bool, tenantAdmin: bool)

// ConsoleRoleState (roles.tsx:24-27)
datatype RoleState = Checking | Ready(roles: RolesResponse) | Unavailable(failure: Failure)

// ConsoleRole | "either" (roles.tsx:22, 55, 78)
datatype Role = OperatorRole | TenantAdminRole | Either

// What RequireRole renders (roles.tsx:83-111)
datatype Rendered = Children | Progress | ReadError | Denied(message: CsString)

// ---- Transliteration ------------------------------------------------------------------------------

// RolesProvider state (roles.tsx:44-49). `roles.data` / `roles.failure` are the
// usePolledResource outputs (`T | null`, `ApiFailure | null`), modelled as Option.
function ProviderState(data: Option<RolesResponse>, failure: Option<Failure>): RoleState
{
  if data.Some? then Ready(data.value)                 // roles.data !== null
  else if failure.Some? then Unavailable(failure.value) // roles.failure !== null
  else Checking
}

// hasRole (roles.tsx:55-61)
function HasRole(state: RoleState, role: Role): bool
{
  if !state.Ready? then
    false
  else
    match role
    case Either => state.roles.operator || state.roles.tenantAdmin
    case OperatorRole => state.roles.operator          // state.roles["operator"]
    case TenantAdminRole => state.roles.tenantAdmin    // state.roles["tenantAdmin"]
}

// DENIED (roles.tsx:63-67)
// Each literal is far below the CsString bound; the explicit lengths let the verifier see it.
function DeniedOperator(): CsString
{
  var s: seq<char> := "Not authorized: this page requires the Operator role.";
  assert |s| == 53;
  s
}

function DeniedTenantAdmin(): CsString
{
  var s: seq<char> := "Not authorized: this page requires the tenant admin role.";
  assert |s| == 57;
  s
}

function DeniedEither(): CsString
{
  var s: seq<char> := "Not authorized: the console is for operators and tenant admins.";
  assert |s| == 63;
  s
}

function DeniedMessage(role: Role): CsString
{
  match role
  case OperatorRole => DeniedOperator()
  case TenantAdminRole => DeniedTenantAdmin()
  case Either => DeniedEither()
}

// RequireRole (roles.tsx:74-112); `state` is useConsoleRoles() (line 81).
function RequireRoleRender(state: RoleState, role: Role): Rendered
{
  if state.Checking? then
    Progress
  else if state.Unavailable? then
    ReadError
  else if !HasRole(state, role) then
    Denied(DeniedMessage(role))
  else
    Children
}

// ---- Properties (frozen) --------------------------------------------------------------------------

// Citation: tests "shows progress, not the page and not a refusal, while the roles are being read" and "says the roles could not be read when the request failed, rather than refusing" (T6 S2 2a), generalised; "replaced by a role guard over that response" (§2.2 line 78)
lemma P1_NoRoleBeforeTheAnswer(state: RoleState, role: Role)
  requires !state.Ready?
  ensures !HasRole(state, role)
{
}

// Citation: tests "refuses an operator a tenant-admin page: the two roles answer different questions" and "admits an operator and a tenant admin alike to a page for either role"; "The landing page requires either role" (§2.2 line 79)
lemma P2_RolesAreTheServersAnswer(r: RolesResponse)
  ensures HasRole(Ready(r), OperatorRole) == r.operator
       && HasRole(Ready(r), TenantAdminRole) == r.tenantAdmin
       && HasRole(Ready(r), Either) == (r.operator || r.tenantAdmin)
{
}

// Citation: the `RequireRole` tests (T6 S2 2a), generalised
lemma P3_GuardRendersByState(state: RoleState, role: Role)
  ensures (RequireRoleRender(state, role) == Children) <==> (state.Ready? && HasRole(state, role))
  ensures state.Checking? ==> RequireRoleRender(state, role) == Progress
  ensures state.Unavailable? ==> RequireRoleRender(state, role) == ReadError
{
}

// Citation: tests "reads the roles once with the session's token and publishes them", "publishes 'checking' and reads nothing before the session has a token", "publishes 'unavailable' when the roles request fails"; "the last good answer is kept while a retry runs" is the code's own comment and not cited
lemma P4_ProviderState(data: Option<RolesResponse>, failure: Option<Failure>)
  ensures ProviderState(data, failure) == (if data.Some? then Ready(data.value) else if failure.Some? then Unavailable(failure.value) else Checking)
{
}
