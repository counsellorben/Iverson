// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T9 S3
// Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/secret-service-clients.yaml the iverson-console-admins expression (352–358)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/secret-service-clients.console-admins.dfy

// Python string unit (shared vocabulary).
newtype CodePoint = x: int | 0 <= x < 0x110000

// ---- Approved axioms (library boundary) ----
type AkEnv

type AkUser

function AkIsGroupMember(env: AkEnv, user: AkUser, group: seq<CodePoint>): bool

function UserUid(user: AkUser): seq<CodePoint>

// ---- Transliteration ----
// name="tenant-admins" (line 358), as Python code points.
const TenantAdminsGroup: seq<CodePoint> :=
  [116, 101, 110, 97, 110, 116, 45, 97, 100, 109, 105, 110, 115]

// Line 358:
//   return ak_is_group_member(request.user, name="tenant-admins") or request.user.uid in {{ .Values.global.operatorSubjects | default (list) | toJson }}
// `env` stands for Authentik's group state seen by ak_is_group_member; `subjects` is the
// Helm-rendered list literal (toJson of global.operatorSubjects, default []), read by Python
// as a list of the same strings. Python `or` short-circuits and both operands are bool, so the
// returned value is exactly the Dafny short-circuit `||`. `x in list` is list membership by
// string equality, i.e. Dafny sequence membership.
function ConsoleAdmins(env: AkEnv, user: AkUser, subjects: seq<seq<CodePoint>>): bool
{
  AkIsGroupMember(env, user, TenantAdminsGroup) || UserUid(user) in subjects
}

// ---- Properties ----
// Citation: "It admits members of `tenant-admins` and subjects on the operator list (§2.4); everyone else gets Authentik's "Permission denied" at authorize." (§2.2 line 66)
lemma P1_TenantAdminAdmitted(env: AkEnv, user: AkUser, subjects: seq<seq<CodePoint>>)
  requires AkIsGroupMember(env, user, TenantAdminsGroup)
  ensures ConsoleAdmins(env, user, subjects)
{
}

// Citation: "The expression is `ak_is_group_member(request.user, name="tenant-admins") or request.user.uid in [<list>]`." (§2.4 line 116)
lemma P2_ListedOperatorAdmitted(env: AkEnv, user: AkUser, subjects: seq<seq<CodePoint>>)
  requires UserUid(user) in subjects
  ensures ConsoleAdmins(env, user, subjects)
{
}

// Citation: "everyone else gets Authentik's "Permission denied" at authorize" (§2.2 line 66)
lemma P3_EveryoneElseRefused(env: AkEnv, user: AkUser, subjects: seq<seq<CodePoint>>)
  requires !AkIsGroupMember(env, user, TenantAdminsGroup) && UserUid(user) !in subjects
  ensures !ConsoleAdmins(env, user, subjects)
{
}
