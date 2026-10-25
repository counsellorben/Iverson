// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T8 S3
// Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml the iverson-admin-orchestrator-workload expression (100–118)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/blueprints-configmap.orchestrator-workload.dfy

// ---------- Shared vocabulary ----------
datatype Option<T> = None | Some(value: T)
newtype CodePoint = x: int | 0 <= x < 0x110000

// A Dafny string literal (Unicode scalar values) as a Python str.
function S(s: string): seq<CodePoint>
{
  seq(|s|, i requires 0 <= i < |s| => s[i] as int as CodePoint)
}

// Python values as decoded from a JWT (JSON); PyOther: a float or any other object.
datatype PyVal = PyNone | PyBool(b: bool) | PyInt(i: int) | PyStr(s: seq<CodePoint>)
               | PyDict(m: map<seq<CodePoint>, PyVal>) | PyList(items: seq<PyVal>) | PyOther

// An expression that raises is a failed policy.
datatype Res = Raised | Ok(b: bool)

// ---------- Approved axioms (library boundary) ----------
type AkEnv

type AkUser

function AkUserBy(env: AkEnv, username: seq<CodePoint>): Option<AkUser>

function AkIsGroupMember(env: AkEnv, user: AkUser, group: seq<CodePoint>): bool

// ---------- Python semantics ----------
// Python truthiness. PyOther (a float or other object) is taken as truthy; every use below
// refuses either way (see the correspondence notes).
function Truthy(v: PyVal): bool
{
  match v
  case PyNone => false
  case PyBool(b) => b
  case PyInt(i) => i != 0
  case PyStr(s) => |s| > 0
  case PyDict(m) => |m| > 0
  case PyList(xs) => |xs| > 0
  case PyOther => true
}

// `x or {}`
function OrEmptyDict(v: PyVal): PyVal
{
  if Truthy(v) then v else PyDict(map[])
}

// `d.get(key)` on a dict: None (PyNone) when absent.
function DictGet(m: map<seq<CodePoint>, PyVal>, key: seq<CodePoint>): PyVal
{
  if key in m then m[key] else PyNone
}

// `isinstance(v, int)`: true for bool too.
predicate IsInt(v: PyVal)
{
  v.PyInt? || v.PyBool?
}

function IntVal(v: PyVal): int
  requires IsInt(v)
{
  match v
  case PyInt(i) => i
  case PyBool(b) => if b then 1 else 0
}

// `str == v`: true only for an equal str.
predicate StrEq(v: PyVal, s: seq<CodePoint>)
{
  v == PyStr(s)
}

// `s in xs` for a list.
predicate ListHasStr(xs: seq<PyVal>, s: seq<CodePoint>)
{
  exists k :: 0 <= k < |xs| && StrEq(xs[k], s)
}

predicate SubAt(sub: seq<CodePoint>, s: seq<CodePoint>, k: int)
{
  0 <= k <= |s| - |sub| && s[k..k + |sub|] == sub
}

predicate IsSubstring(sub: seq<CodePoint>, s: seq<CodePoint>)
{
  exists k :: 0 <= k <= |s| - |sub| && SubAt(sub, s, k)
}

// `s in c` for a str s: dict key, list member, substring; TypeError otherwise.
function PyContainsStr(c: PyVal, s: seq<CodePoint>): Res
{
  match c
  case PyDict(m) => Ok(s in m)
  case PyList(xs) => Ok(ListHasStr(xs, s))
  case PyStr(t) => Ok(IsSubstring(s, t))
  case _ => Raised
}

// ---------- Constants (the expression's literals) ----------
const IssKey: seq<CodePoint> := S("iss")
const SubKey: seq<CodePoint> := S("sub")
const AudKey: seq<CodePoint> := S("aud")
const IatKey: seq<CodePoint> := S("iat")
const ExpKey: seq<CodePoint> := S("exp")
const K8sKey: seq<CodePoint> := S("kubernetes.io")
const PodKey: seq<CodePoint> := S("pod")
const OrchestratorAudience: seq<CodePoint> := S("iverson-authentik-orchestrator")
const WorkloadUsername: seq<CodePoint> := S("iverson-admin-orchestrator-workload")
const OrchestratorsGroup: seq<CodePoint> := S("iverson-admin-orchestrators")

// "system:serviceaccount:{{ .Release.Namespace }}:{{ .Release.Name }}-api"
function ExpectedSub(ns: seq<CodePoint>, rel: seq<CodePoint>): seq<CodePoint>
{
  S("system:serviceaccount:") + ns + S(":") + rel + S("-api")
}

// `(request.context.get("oauth_jwt") or {}).get(key)`, None when absent (or when the value is not
// a dict, where `.get` raises and the expression refuses).
function JwtGet(jwt: Option<PyVal>, key: seq<CodePoint>): Option<PyVal>
{
  var d := if jwt.Some? then OrEmptyDict(jwt.value) else PyDict(map[]);
  if d.PyDict? && key in d.m then Some(d.m[key]) else None
}

// ---------- Transliteration ----------
// jwtCtx: request.context.get("oauth_jwt") (None: absent); issuer: the value of the rendered
// "{{ $issuer }}" literal; ns, rel: .Release.Namespace and .Release.Name.
function WorkloadExpr(jwtCtx: Option<PyVal>, issuer: seq<CodePoint>, ns: seq<CodePoint>,
                      rel: seq<CodePoint>, env: AkEnv): Res
{
  // jwt = request.context.get("oauth_jwt") or {}
  var jwt := if jwtCtx.Some? then OrEmptyDict(jwtCtx.value) else PyDict(map[]);
  // aud = jwt.get("aud")   (AttributeError on a non-dict)
  if !jwt.PyDict? then Raised
  else
    var aud := DictGet(jwt.m, AudKey);
    // auds = aud if isinstance(aud, list) else [aud]
    var auds := if aud.PyList? then aud.items else [aud];
    // iat = jwt.get("iat"); exp = jwt.get("exp")
    var iat := DictGet(jwt.m, IatKey);
    var exp := DictGet(jwt.m, ExpKey);
    // k8s = jwt.get("kubernetes.io") or {}
    var k8s := OrEmptyDict(DictGet(jwt.m, K8sKey));
    // user = ak_user_by(username="iverson-admin-orchestrator-workload")
    var user := AkUserBy(env, WorkloadUsername);
    // return ( ... and ... )
    if !StrEq(DictGet(jwt.m, IssKey), issuer) then Ok(false)
    else if !StrEq(DictGet(jwt.m, SubKey), ExpectedSub(ns, rel)) then Ok(false)
    else if !ListHasStr(auds, OrchestratorAudience) then Ok(false)
    else if !IsInt(iat) then Ok(false)
    else if !IsInt(exp) then Ok(false)
    else if !(IntVal(exp) - IntVal(iat) <= 600) then Ok(false)
    else
      var hasPod := PyContainsStr(k8s, PodKey);
      if hasPod.Raised? then Raised
      else if !hasPod.b then Ok(false)
      else if user.None? then Ok(false)
      else Ok(AkIsGroupMember(env, user.value, OrchestratorsGroup))
}

// Exception -> False.
function PolicyResult(r: Res): bool
{
  match r
  case Raised => false
  case Ok(b) => b
}

// The policy admits the token under some rendering of the release namespace and name.
ghost predicate OrchestratorWorkload(jwt: Option<PyVal>, issuer: seq<CodePoint>, env: AkEnv)
{
  exists ns: seq<CodePoint>, rel: seq<CodePoint> :: PolicyResult(WorkloadExpr(jwt, issuer, ns, rel, env))
}

// ---------- Helper ----------
lemma IssuerMismatchRefusesEveryRelease(jwt: Option<PyVal>, issuer: seq<CodePoint>, env: AkEnv,
                                         ns: seq<CodePoint>, rel: seq<CodePoint>)
  requires JwtGet(jwt, IssKey) != Some(PyStr(issuer))
  ensures !PolicyResult(WorkloadExpr(jwt, issuer, ns, rel, env))
{
  var d := if jwt.Some? then OrEmptyDict(jwt.value) else PyDict(map[]);
  if d.PyDict? {
    assert !StrEq(DictGet(d.m, IssKey), issuer);
  }
}

// ---------- Properties ----------
// Citation: "Both pins (orchestrator and admin-automation) compare `iss` exactly with a new required value `authentik.serviceAccountIssuer`." (§2.6 line 181)
lemma P1_AnotherIssuerRefused(jwt: Option<PyVal>, issuer: seq<CodePoint>, env: AkEnv)
  requires JwtGet(jwt, IssKey) != Some(PyStr(issuer))
  ensures !OrchestratorWorkload(jwt, issuer, env)
{
  forall ns: seq<CodePoint>, rel: seq<CodePoint>
    ensures !PolicyResult(WorkloadExpr(jwt, issuer, ns, rel, env))
  {
    IssuerMismatchRefusesEveryRelease(jwt, issuer, env, ns, rel);
  }
}
