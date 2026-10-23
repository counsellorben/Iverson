// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T8 S3
// Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml the iverson-admin-orchestrator-tokenreview expression (141–168)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/blueprints-configmap.orchestrator-tokenreview.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

newtype CodePoint = x: int | 0 <= x < 0x110000

// A JSON / Python value.
datatype PyVal =
  | PyNone
  | PyBool(b: bool)
  | PyInt(i: int)
  | PyStr(s: seq<CodePoint>)
  | PyDict(m: map<seq<CodePoint>, PyVal>)
  | PyList(items: seq<PyVal>)
  | PyOther

// request.context.get("oauth_jwt") (None: key missing);
// request.http_request.POST["client_assertion"] (None: key missing, so KeyError).
datatype ReviewReq = ReviewReq(oauthJwt: Option<PyVal>, formAssertion: Option<seq<CodePoint>>)

// requests.post(..., timeout=2): raises, or answers a status and a body whose
// .json() either raises (None) or yields a value.
datatype PostResult = PostRaised | PostResponse(status: int, json: Option<PyVal>)

// String literals of the expression (Python str = seq<CodePoint>).
const S_status: seq<CodePoint> := [115, 116, 97, 116, 117, 115]                              // "status"
const S_authenticated: seq<CodePoint> := [97, 117, 116, 104, 101, 110, 116, 105, 99, 97, 116, 101, 100] // "authenticated"
const S_user: seq<CodePoint> := [117, 115, 101, 114]                                          // "user"
const S_username: seq<CodePoint> := [117, 115, 101, 114, 110, 97, 109, 101]                   // "username"

// Python truthiness. PyOther is a plain object (float, etc.): a non-zero float is
// truthy, an arbitrary object defaults to truthy; it is never consulted by a property
// except through Truthy itself.
predicate Truthy(v: PyVal)
{
  match v
  case PyNone => false
  case PyBool(b) => b
  case PyInt(i) => i != 0
  case PyStr(s) => |s| > 0
  case PyDict(m) => |m| > 0
  case PyList(items) => |items| > 0
  case PyOther => true
}

// ---------------------------------------------------------------------------
// Python semantics used by the expression
// ---------------------------------------------------------------------------

// An evaluation step: an exception was raised, or a value was produced.
datatype Ex<T> = Exc | Ok(v: T)

// obj.get(key): a dict answers its value or None; anything else has no .get
// (AttributeError).
function DictGet(obj: PyVal, key: seq<CodePoint>): Ex<PyVal>
{
  if obj.PyDict? then Ok(if key in obj.m then obj.m[key] else PyNone) else Exc
}

// `x or {}`
function OrEmpty(v: PyVal): PyVal
{
  if Truthy(v) then v else PyDict(map[])
}

// ---------------------------------------------------------------------------
// Transliteration (blueprints-configmap.yaml 147–168)
// ---------------------------------------------------------------------------

// The body of the `try:` (lines 148–166). Exc: an exception escaped the body.
function TryBody(req: ReviewReq, post: PostResult, expected: seq<CodePoint>): Ex<bool>
{
  // 148: if not request.context.get("oauth_jwt"): return False
  var jwt := if req.oauthJwt.Some? then req.oauthJwt.value else PyNone;
  if !Truthy(jwt) then Ok(false)
  // 150: assertion = request.http_request.POST["client_assertion"]  (KeyError when missing)
  else if req.formAssertion.None? then Exc
  // 151–159: response = requests.post(...)
  else if post.PostRaised? then Exc
  // 160–161: if not 200 <= response.status_code < 300: return False
  else if !(200 <= post.status < 300) then Ok(false)
  // 162: status = response.json().get("status") or {}
  else if post.json.None? then Exc
  else match DictGet(post.json.value, S_status)
    case Exc => Exc
    case Ok(s0) =>
      var status := OrEmpty(s0);
      // 164: status.get("authenticated") is True
      match DictGet(status, S_authenticated)
      case Exc => Exc
      case Ok(a) =>
        if a != PyBool(true) then Ok(false)
        // 165: and (status.get("user") or {}).get("username") == "<expected>"
        else match DictGet(status, S_user)
          case Exc => Exc
          case Ok(u0) =>
            match DictGet(OrEmpty(u0), S_username)
            case Exc => Exc
            case Ok(n) => Ok(n == PyStr(expected))
}

// 147, 167–168: try: ... except Exception: return False
predicate Pin(req: ReviewReq, post: PostResult, expected: seq<CodePoint>)
{
  match TryBody(req, post, expected)
  case Exc => false
  case Ok(b) => b
}

// ---------------------------------------------------------------------------
// Property vocabulary
// ---------------------------------------------------------------------------

// {"status": {"authenticated": auth, "user": {"username": user}}}
function ReviewBody(auth: PyVal, user: PyVal): PyVal
{
  PyDict(map[S_status := PyDict(map[S_authenticated := auth, S_user := PyDict(map[S_username := user])])])
}

// v["status"]["authenticated"] when every step is a dict holding the key.
function ReviewAuthenticated(v: PyVal): Option<PyVal>
{
  if v.PyDict? && S_status in v.m
     && v.m[S_status].PyDict? && S_authenticated in v.m[S_status].m
  then Some(v.m[S_status].m[S_authenticated])
  else None
}

// v["status"]["user"]["username"] when every step is a dict holding the key.
function ReviewUsername(v: PyVal): Option<PyVal>
{
  if v.PyDict? && S_status in v.m
     && v.m[S_status].PyDict? && S_user in v.m[S_status].m
     && v.m[S_status].m[S_user].PyDict? && S_username in v.m[S_status].m[S_user].m
  then Some(v.m[S_status].m[S_user].m[S_username])
  else None
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: design TokenReviewPin.P1_SuccessAdmitted: "It refuses (`invalid_grant`) on any exception, on any response status outside 2xx (a successful review answers 201)" (§2.6 line 175)
lemma P1_SuccessAdmitted(jwt: PyVal, assertion: seq<CodePoint>, expected: seq<CodePoint>)
  requires Truthy(jwt)
  ensures Pin(ReviewReq(Some(jwt), Some(assertion)), PostResponse(201, Some(ReviewBody(PyBool(true), PyStr(expected)))), expected)
{
  var body := ReviewBody(PyBool(true), PyStr(expected));
  var status := PyDict(map[S_authenticated := PyBool(true), S_user := PyDict(map[S_username := PyStr(expected)])]);
  assert S_authenticated != S_user;
  assert DictGet(body, S_status) == Ok(status);
  assert |status.m| > 0 by { assert S_authenticated in status.m; }
  assert OrEmpty(status) == status;
  assert DictGet(status, S_authenticated) == Ok(PyBool(true));
  var user := PyDict(map[S_username := PyStr(expected)]);
  assert DictGet(status, S_user) == Ok(user);
  assert |user.m| > 0 by { assert S_username in user.m; }
  assert OrEmpty(user) == user;
  assert DictGet(user, S_username) == Ok(PyStr(expected));
}

// Citation: design TokenReviewPin.P2_ErrorsRefused: same sentence (§2.6 line 175)
lemma P2_ErrorsRefused(req: ReviewReq, post: PostResult, expected: seq<CodePoint>)
  requires post.PostRaised? || (post.PostResponse? && !(200 <= post.status < 300))
  ensures !Pin(req, post, expected)
{
}

// Helper: an admitting pin has read status.authenticated = True and
// status.user.username = expected along an all-dict path.
lemma PinReadsIdentity(req: ReviewReq, post: PostResult, expected: seq<CodePoint>)
  requires post.PostResponse? && post.json.Some?
  ensures Pin(req, post, expected) ==>
            ReviewAuthenticated(post.json.value) == Some(PyBool(true))
            && ReviewUsername(post.json.value) == Some(PyStr(expected))
{
  if Pin(req, post, expected) {
    var v := post.json.value;
    assert v.PyDict?;
    var s0 := if S_status in v.m then v.m[S_status] else PyNone;
    var status := OrEmpty(s0);
    assert DictGet(v, S_status) == Ok(s0);
    assert status.PyDict?;
    assert S_authenticated in status.m;
    assert status.m[S_authenticated] == PyBool(true);
    assert S_status in v.m && Truthy(s0);
    assert status == v.m[S_status];
    var u0 := if S_user in status.m then status.m[S_user] else PyNone;
    var user := OrEmpty(u0);
    assert DictGet(status, S_user) == Ok(u0);
    assert user.PyDict?;
    assert S_username in user.m;
    assert user.m[S_username] == PyStr(expected);
    assert S_user in status.m && Truthy(u0);
    assert user == status.m[S_user];
  }
}

// Citation: design TokenReviewPin.P3_WrongIdentityRefused: "It requires `status.authenticated` true and `status.user.username` = `system:serviceaccount:<ns>:<rel>-api`." (§2.6 line 175)
lemma P3_WrongIdentityRefused(req: ReviewReq, post: PostResult, expected: seq<CodePoint>)
  requires post.PostResponse? && post.json.Some?
  requires ReviewAuthenticated(post.json.value) != Some(PyBool(true)) || ReviewUsername(post.json.value) != Some(PyStr(expected))
  ensures !Pin(req, post, expected)
{
  PinReadsIdentity(req, post, expected);
}

// Citation: "It keys on `context["oauth_jwt"]` and the request, not `request.user`, which is anonymous on a first exchange." (§2.6 line 176); test `TokenReviewExpression_ReviewsTheRawAssertion_AndRefusesEveryFailure` (`if not request.context.get("oauth_jwt"):`)
lemma P4_NoFederatedJwtRefused(req: ReviewReq, post: PostResult, expected: seq<CodePoint>)
  requires req.oauthJwt.None? || !Truthy(req.oauthJwt.value)
  ensures !Pin(req, post, expected)
{
}

// Citation: "The raw assertion is read from the token request's form body." (§2.6 line 177) with "It refuses (`invalid_grant`) on any exception" (line 175); test `TokenReviewExpression_…` ("every step, the form read included, is inside the try")
lemma P5_MissingAssertionRefused(req: ReviewReq, post: PostResult, expected: seq<CodePoint>)
  requires req.formAssertion.None?
  ensures !Pin(req, post, expected)
{
}
