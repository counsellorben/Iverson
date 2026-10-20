// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ 19d74cfb95c4ccb8ceca9e939494828f3359c3a5
// Source: §2.2 "API" (line 69) and §2.4 "The list", "One function", "Compose", lines 69-119
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/RoleFunctions.dfy
// Round: 1

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// A token's principal: its `sub`, its `scope` values and its `groups` claim.
datatype Principal = Principal(sub: string, scopes: set<string>, groups: set<string>)

// Per-install configuration seen by the API:
//   operatorSubjects  = chart value `global.operatorSubjects` (line 104),
//                       rendered as `Authorization__OperatorSubjects__N` (line 116)
//   fallback          = API setting `Authorization:OperatorGroupFallback`,
//                       unset by default (line 119)
datatype Config = Config(operatorSubjects: seq<string>, fallback: Option<string>)

// Line 108: true when the principal's `sub` is on the list or its `scope`
// contains `admin`. Line 119: the compose fallback adds "or `groups`
// contains this name" to that function. Cases kept in spec order:
// the list, the `admin` scope, the compose fallback (line 111).
predicate IsOperator(p: Principal, c: Config)
{
  p.sub in c.operatorSubjects
  || "admin" in p.scopes
  || (c.fallback.Some? && c.fallback.value in p.groups)
}

// Line 111: SchemaAdminAuthorizationPolicy = OperatorAuthorizationPolicy.IsSatisfiedBy
// (the list, the `admin` scope, or the compose fallback), or scope `schema_admin`.
predicate IsSchemaAdmin(p: Principal, c: Config)
{
  IsOperator(p, c) || "schema_admin" in p.scopes
}

// TenantAdmin is an existing policy the cited lines do not define. Modelled as
// membership of `tenant-admins` (the group §2.2 line 66 and §2.4 line 116 use
// for tenant admins). No property below depends on its body.
predicate IsTenantAdmin(p: Principal, c: Config)
{
  "tenant-admins" in p.groups
}

// Line 69: ConsoleAdmin = Operator (§2.4) or TenantAdmin.
predicate IsConsoleAdmin(p: Principal, c: Config)
{
  IsOperator(p, c) || IsTenantAdmin(p, c)
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "`SchemaAdminAuthorizationPolicy`: `OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` (the list, the `admin` scope, or the compose fallback), or scope `schema_admin`, which keeps "Operator implies SchemaAdmin"" (§2.4 line 111)
lemma P1_OperatorImpliesSchemaAdmin(p: Principal, c: Config)
  ensures IsOperator(p, c) ==> IsSchemaAdmin(p, c)
{
}

// Citation: "The `groups` claim no longer yields Operator anywhere on Helm." (§2.4 line 114) with "API setting `Authorization:OperatorGroupFallback` (unset by default)" (line 119)
lemma P2_GroupsIrrelevantWithoutFallback(p: Principal, q: Principal, c: Config)
  requires c.fallback.None?
  requires p.sub == q.sub && p.scopes == q.scopes
  ensures IsOperator(p, c) == IsOperator(q, c)
{
}

// Citation: "Admin-automation keeps Operator through its `admin` scope." (§2.4 line 106)
lemma P3_AdminScopeIsOperator(p: Principal, c: Config)
  requires "admin" in p.scopes
  ensures IsOperator(p, c)
{
}

// Citation: "A new assertion policy `ConsoleAdmin` = Operator (§2.4) or TenantAdmin." (§2.2 line 69)
lemma P4_OperatorImpliesConsoleAdmin(p: Principal, c: Config)
  ensures IsOperator(p, c) ==> IsConsoleAdmin(p, c)
{
}

// Citation: "(the list, the `admin` scope, or the compose fallback)" (§2.4 line 111)
lemma P5_FallbackGroupIsSchemaAdmin(p: Principal, c: Config)
  requires c.fallback.Some? && c.fallback.value in p.groups
  ensures IsSchemaAdmin(p, c)
{
}
