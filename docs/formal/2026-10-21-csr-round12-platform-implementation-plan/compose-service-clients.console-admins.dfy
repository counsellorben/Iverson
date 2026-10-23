// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T9 S3
// Source: Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml the iverson-console-admins expression (194–199)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/compose-service-clients.console-admins.dfy

// Python strings: sequences of Unicode code points.
newtype CodePoint = x: int | 0 <= x < 0x110000

// Approved axioms (library boundary: Authentik expression-policy environment).
type AkEnv

type AkUser

function AkIsGroupMember(env: AkEnv, user: AkUser, group: seq<CodePoint>): bool

// String literals of the expression, as Python code-point sequences.
const TenantAdminsGroup: seq<CodePoint> := ['t' as CodePoint, 'e' as CodePoint, 'n' as CodePoint, 'a' as CodePoint, 'n' as CodePoint, 't' as CodePoint, '-' as CodePoint, 'a' as CodePoint, 'd' as CodePoint, 'm' as CodePoint, 'i' as CodePoint, 'n' as CodePoint, 's' as CodePoint]
const OperatorsGroup: seq<CodePoint> := ['o' as CodePoint, 'p' as CodePoint, 'e' as CodePoint, 'r' as CodePoint, 'a' as CodePoint, 't' as CodePoint, 'o' as CodePoint, 'r' as CodePoint, 's' as CodePoint]

// service-clients.yaml:199
//   return ak_is_group_member(request.user, name="tenant-admins") or ak_is_group_member(request.user, name="operators")
// `request.user` is the `user` parameter; the Authentik evaluation context is `env`.
// Python `or` short-circuits and, over two bools, returns a bool; Dafny `||` matches.
function ComposeConsoleAdmins(env: AkEnv, user: AkUser): bool
{
  AkIsGroupMember(env, user, TenantAdminsGroup) || AkIsGroupMember(env, user, OperatorsGroup)
}

// Citation: "The `operators` group and the bypass user's membership stay in `compose-only/service-clients.yaml`, together with a compose copy of the console sign-in policy that reads that group." (§2.4 line 120)
lemma P1_ComposeAdmitsTheTwoGroups(env: AkEnv, user: AkUser)
  ensures ComposeConsoleAdmins(env, user) <==> (AkIsGroupMember(env, user, TenantAdminsGroup) || AkIsGroupMember(env, user, OperatorsGroup))
{
}

// Sanity: the constants spell the literals of line 199.
lemma ConstantsSpellTheLiterals()
  ensures |TenantAdminsGroup| == 13 && |OperatorsGroup| == 9
  ensures TenantAdminsGroup != OperatorsGroup
{
}
