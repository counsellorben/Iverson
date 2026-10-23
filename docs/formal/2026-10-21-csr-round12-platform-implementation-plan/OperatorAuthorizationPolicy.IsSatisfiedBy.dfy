// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T4 S3
// Source: Iverson.Server/Iverson.Api/OperatorAuthorizationPolicy.cs IsSatisfiedBy (14–25); with SchemaAdminAuthorizationPolicy.cs IsSatisfiedBy (11–18), ConsoleAdminAuthorizationPolicy.cs IsSatisfiedBy (10–12), OperatorSubjects.cs FromConfiguration (21–25), and the unchanged TenantAdminAuthorizationPolicy.IsSatisfiedBy
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/OperatorAuthorizationPolicy.IsSatisfiedBy.dfy

// ---------------------------------------------------------------------------
// Shared modelling vocabulary
// ---------------------------------------------------------------------------

datatype Option<+T> = None | Some(value: T)

// C# string: a sequence of UTF-16 code units (--unicode-char false), Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

// ---------------------------------------------------------------------------
// Library boundary
// ---------------------------------------------------------------------------

// System.Security.Claims.Claim: Type and Value are non-null strings.
datatype Claim = Claim(ctype: CsString, value: CsString)

// A ClaimsPrincipal is modelled as the claims of all its identities, in order.

// ClaimsPrincipal.FindFirst(type)?.Value: the value of the first claim of that type.
function FindFirst(p: seq<Claim>, t: CsString): Option<CsString>
  decreases |p|
{
  if |p| == 0 then None
  else if p[0].ctype == t then Some(p[0].value)
  else FindFirst(p[1..], t)
}

// ClaimsPrincipal.FindAll(type): every claim of that type, in order.
function FindAll(p: seq<Claim>, t: CsString): seq<Claim>
  decreases |p|
{
  if |p| == 0 then []
  else (if p[0].ctype == t then [p[0]] else []) + FindAll(p[1..], t)
}

// The principal carries a claim of type t whose value is v.
predicate HasClaim(p: seq<Claim>, t: CsString, v: CsString)
{
  exists c :: c in p && c.ctype == t && c.value == v
}

// Enumerable.Any(c => c.Value == v) (string == is ordinal).
predicate AnyValueEquals(cs: seq<Claim>, v: CsString)
{
  exists i :: 0 <= i < |cs| && cs[i].value == v
}

// Enumerable.Select(c => c.Value).
function SelectValues(cs: seq<Claim>): seq<CsString>
{
  seq(|cs|, i requires 0 <= i < |cs| => cs[i].value)
}

// Index of the first ' ' in s, or |s| when there is none.
function IndexOfSpace(s: CsString): (i: nat)
  ensures i <= |s|
  ensures i < |s| ==> s[i] == ' '
  ensures forall j :: 0 <= j < i ==> s[j] != ' '
  decreases |s|
{
  if |s| == 0 then 0
  else if s[0] == ' ' then 0
  else 1 + IndexOfSpace(s[1..])
}

// s.Split(' ', StringSplitOptions.RemoveEmptyEntries).
function ScopeTokens(s: CsString): seq<CsString>
  decreases |s|
{
  var i := IndexOfSpace(s);
  var head: CsString := s[..i];
  var rest := if i < |s| then ScopeTokens(s[i + 1..]) else [];
  (if |head| > 0 then [head] else []) + rest
}

// OperatorSubjects record: IReadOnlySet<string> (HashSet, StringComparer.Ordinal) and string?.
datatype OperatorSubjects = OperatorSubjects(Subjects: set<CsString>, GroupFallback: Option<CsString>)

// IConfiguration (approved axioms).
type Config

function ConfigValue(cfg: Config, key: CsString): Option<CsString>

function ConfigStringArray(cfg: Config, section: CsString): Option<seq<CsString>>

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// OperatorAuthorizationPolicy.cs 14–25
function IsOperator(p: seq<Claim>, os: OperatorSubjects): bool
{
  var sub := FindFirst(p, "sub");
  if sub.Some? && sub.value in os.Subjects then
    true
  else
    var scope := FindFirst(p, "scope");
    if scope.Some? && "admin" in ScopeTokens(scope.value) then
      true
    else
      os.GroupFallback.Some?
      && AnyValueEquals(FindAll(p, "groups"), os.GroupFallback.value)
}

// SchemaAdminAuthorizationPolicy.cs 11–18
function IsSchemaAdmin(p: seq<Claim>, os: OperatorSubjects): bool
{
  if IsOperator(p, os) then
    true
  else
    var scope := FindFirst(p, "scope");
    scope.Some? && "schema_admin" in ScopeTokens(scope.value)
}

// TenantAdminAuthorizationPolicy.cs 5 (unchanged)
function IsTenantAdmin(groupClaims: seq<CsString>): bool
{
  "tenant-admins" in groupClaims
}

// ConsoleAdminAuthorizationPolicy.cs 10–12
function IsConsoleAdmin(p: seq<Claim>, os: OperatorSubjects): bool
{
  IsOperator(p, os)
  || IsTenantAdmin(SelectValues(FindAll(p, "groups")))
}

// OperatorSubjects.cs 21–25
function FromConfiguration(cfg: Config): OperatorSubjects
{
  var arr := ConfigStringArray(cfg, "Authorization:OperatorSubjects");
  var items: seq<CsString> := if arr.Some? then arr.value else [];
  var fb := ConfigValue(cfg, "Authorization:OperatorGroupFallback");
  OperatorSubjects(
    set x | x in items,
    if fb.Some? && |fb.value| > 0 then Some(fb.value) else None)
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma FindAllMembership(p: seq<Claim>, t: CsString, c: Claim)
  ensures c in FindAll(p, t) <==> (c in p && c.ctype == t)
  decreases |p|
{
  if |p| == 0 {
  } else {
    FindAllMembership(p[1..], t, c);
    assert p == [p[0]] + p[1..];
  }
}

lemma GroupsAnyIffHasClaim(p: seq<Claim>, v: CsString)
  ensures AnyValueEquals(FindAll(p, "groups"), v) <==> HasClaim(p, "groups", v)
{
  var cs := FindAll(p, "groups");
  if AnyValueEquals(cs, v) {
    var i :| 0 <= i < |cs| && cs[i].value == v;
    FindAllMembership(p, "groups", cs[i]);
    assert cs[i] in p && cs[i].ctype == "groups" && cs[i].value == v;
  }
  if HasClaim(p, "groups", v) {
    var c :| c in p && c.ctype == "groups" && c.value == v;
    FindAllMembership(p, "groups", c);
    var i :| 0 <= i < |cs| && cs[i] == c;
    assert cs[i].value == v;
  }
}

lemma GroupsSelectIffHasClaim(p: seq<Claim>, v: CsString)
  ensures v in SelectValues(FindAll(p, "groups")) <==> HasClaim(p, "groups", v)
{
  var cs := FindAll(p, "groups");
  var vs := SelectValues(cs);
  if v in vs {
    var i :| 0 <= i < |vs| && vs[i] == v;
    FindAllMembership(p, "groups", cs[i]);
    assert cs[i] in p && cs[i].ctype == "groups" && cs[i].value == v;
  }
  if HasClaim(p, "groups", v) {
    var c :| c in p && c.ctype == "groups" && c.value == v;
    FindAllMembership(p, "groups", c);
    var i :| 0 <= i < |cs| && cs[i] == c;
    assert vs[i] == v;
  }
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: design RoleFunctions.P1_OperatorImpliesSchemaAdmin: "`SchemaAdminAuthorizationPolicy`: `OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` (the list, the `admin` scope, or the compose fallback), or scope `schema_admin`, which keeps "Operator implies SchemaAdmin"" (§2.4 line 111)
lemma P1_OperatorImpliesSchemaAdmin(p: seq<Claim>, os: OperatorSubjects)
  ensures IsOperator(p, os) ==> IsSchemaAdmin(p, os)
{
}

// Citation: design RoleFunctions.P2_GroupsIrrelevantWithoutFallback: "The `groups` claim no longer yields Operator anywhere on Helm." (§2.4 line 114) with "API setting `Authorization:OperatorGroupFallback` (unset by default)" (line 119)
lemma P2_GroupsIrrelevantWithoutFallback(p: seq<Claim>, q: seq<Claim>, os: OperatorSubjects)
  requires os.GroupFallback.None?
  requires FindFirst(p, "sub") == FindFirst(q, "sub") && FindFirst(p, "scope") == FindFirst(q, "scope")
  ensures IsOperator(p, os) == IsOperator(q, os)
{
}

// Citation: design RoleFunctions.P3_AdminScopeIsOperator: "Admin-automation keeps Operator through its `admin` scope." (§2.4 line 106)
lemma P3_AdminScopeIsOperator(p: seq<Claim>, os: OperatorSubjects)
  requires FindFirst(p, "scope").Some? && "admin" in ScopeTokens(FindFirst(p, "scope").value)
  ensures IsOperator(p, os)
{
}

// Citation: design RoleFunctions.P4_OperatorImpliesConsoleAdmin: "A new assertion policy `ConsoleAdmin` = Operator (§2.4) or TenantAdmin." (§2.2 line 69)
lemma P4_OperatorImpliesConsoleAdmin(p: seq<Claim>, os: OperatorSubjects)
  ensures IsOperator(p, os) ==> IsConsoleAdmin(p, os)
{
}

// Citation: design RoleFunctions.P5_FallbackGroupIsSchemaAdmin: "(the list, the `admin` scope, or the compose fallback)" (§2.4 line 111)
lemma P5_FallbackGroupIsSchemaAdmin(p: seq<Claim>, os: OperatorSubjects)
  requires os.GroupFallback.Some? && HasClaim(p, "groups", os.GroupFallback.value)
  ensures IsSchemaAdmin(p, os)
{
  GroupsAnyIffHasClaim(p, os.GroupFallback.value);
}

// Citation: "`OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)` is true when the principal's `sub` is on the list or its `scope` contains `admin`." (§2.4 line 108); test `IsSatisfiedBy_SubjectOnTheList_ReturnsTrue`
lemma P6_ListedSubjectIsOperator(p: seq<Claim>, os: OperatorSubjects)
  requires FindFirst(p, "sub").Some? && FindFirst(p, "sub").value in os.Subjects
  ensures IsOperator(p, os)
{
}

// Citation: "A new assertion policy `ConsoleAdmin` = Operator (§2.4) or TenantAdmin." (§2.2 line 69); test `IsSatisfiedBy_SignedInUserWithNeitherRole_ReturnsFalse`
lemma P7_ConsoleAdminIsOperatorOrTenantAdmin(p: seq<Claim>, os: OperatorSubjects)
  ensures IsConsoleAdmin(p, os) <==> (IsOperator(p, os) || HasClaim(p, "groups", "tenant-admins"))
{
  GroupsSelectIffHasClaim(p, "tenant-admins");
}

// Citation: "API setting `Authorization:OperatorGroupFallback` (unset by default)" (§2.4 line 119); test `FromConfiguration_NothingSet_IsAnEmptyListAndNoFallback` (`null`, `""`)
lemma P8_EmptyFallbackIsUnset(cfg: Config)
  requires ConfigValue(cfg, "Authorization:OperatorGroupFallback") == None || ConfigValue(cfg, "Authorization:OperatorGroupFallback") == Some("")
  ensures FromConfiguration(cfg).GroupFallback.None?
{
}
