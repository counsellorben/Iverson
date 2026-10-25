// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T1 S3
// Source: Iverson.Server/Iverson.Api/Tenancy/IdpAdminClient.cs ListUsersByTenantAsync (148–206), GetUserAsync (214–238), ReadPk (376–382), TenantIdOf (385–390)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/IdpAdminClient.TenantReads.dfy

// ---------------------------------------------------------------------------
// Shared modelling vocabulary
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// C# string: UTF-16 code units (--unicode-char false), Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

newtype int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

// System.Text.Json document, as parsed. JNumber keeps the number's raw text (GetRawText);
// JObject keeps its properties in document order, duplicates included.
datatype Json = JNull | JBool(b: bool) | JNumber(raw: CsString) | JString(s: CsString) | JArray(items: seq<Json>) | JObject(fields: seq<(CsString, Json)>)

// One HttpClient.GetAsync + JsonDocument.ParseAsync: body None = the body is not JSON
// (ParseAsync throws); Transport = HttpClient threw.
datatype HttpAnswer = Response(status: int, body: Option<Json>) | Transport

// IIdpAdminClient.cs:8 `record IdpUser(string Id, string Username, string Email, string Uid)`.
// Each field is filled through `GetString()!` (or ReadPk's), which lets a JSON null through,
// so each is an Option.
datatype IdpUser = IdpUser(Id: Option<CsString>, Username: Option<CsString>, Email: Option<CsString>, Uid: Option<CsString>)

// IIdpAdminClient.cs:16 `record IdpUserRecord(...)`. Id is never null in a built record
// (a null pk fails `pk != userId` first); Username/Email/Uid and each group go through
// `GetString()!` and may be null.
datatype IdpUserRecord = IdpUserRecord(Id: CsString, Username: Option<CsString>, Email: Option<CsString>, Uid: Option<CsString>, IsActive: bool, Groups: seq<Option<CsString>>, TenantId: Option<CsString>)

datatype GetUserResult = Found(record: IdpUserRecord) | Null | Threw

// sources[i]: the JSON element users[i] was read from (ghost); paths: every request path sent,
// in order; Unfinished: the page fuel ran out.
datatype ListResult = Users(users: seq<IdpUser>, sources: seq<Json>, paths: seq<seq<char>>) | ListThrew(paths: seq<seq<char>>) | Unfinished(paths: seq<seq<char>>)

// ---------------------------------------------------------------------------
// Approved axioms (library boundary)
// ---------------------------------------------------------------------------

type Server

function Get(server: Server, path: seq<char>): HttpAnswer

function TenantFilter(tenantId: CsString): CsString

function JsonGetInt32(raw: CsString): Option<int32>

// ---------------------------------------------------------------------------
// Modelled library calls (JsonElement over Json)
// ---------------------------------------------------------------------------

// Property lookup: ordinal, case-sensitive, last definition of a duplicated name wins.
function LastLookup(fields: seq<(CsString, Json)>, key: seq<char>): Option<Json>
  decreases |fields|
{
  if |fields| == 0 then None
  else if fields[|fields| - 1].0 == key then Some(fields[|fields| - 1].1)
  else LastLookup(fields[..|fields| - 1], key)
}

datatype GetResult = Got(v: Json) | GetThrew

// JsonElement.GetProperty: a non-object throws InvalidOperationException; a missing name
// throws KeyNotFoundException.
function GetProperty(e: Json, name: seq<char>): GetResult
{
  if !e.JObject? then GetThrew
  else match LastLookup(e.fields, name)
    case None => GetThrew
    case Some(v) => Got(v)
}

datatype TryGetResult = TGHit(v: Json) | TGMiss | TGThrew

// JsonElement.TryGetProperty: a non-object throws InvalidOperationException.
function TryGetProperty(e: Json, name: seq<char>): TryGetResult
{
  if !e.JObject? then TGThrew
  else match LastLookup(e.fields, name)
    case None => TGMiss
    case Some(v) => TGHit(v)
}

datatype StrResult = Str(s: Option<CsString>) | StrThrew

// JsonElement.GetString: String -> the string, Null -> null, any other kind throws.
function GetString(e: Json): StrResult
{
  match e
  case JString(s) => Str(Some(s))
  case JNull => Str(None)
  case _ => StrThrew
}

// x.GetProperty(name).GetString()!
function PropString(e: Json, name: seq<char>): StrResult
{
  match GetProperty(e, name)
  case GetThrew => StrThrew
  case Got(v) => GetString(v)
}

datatype BoolResult = BoolOk(b: bool) | BoolThrew

// x.GetProperty(name).GetBoolean(): True/False -> value, any other kind throws.
function PropBool(e: Json, name: seq<char>): BoolResult
{
  match GetProperty(e, name)
  case GetThrew => BoolThrew
  case Got(v) => if v.JBool? then BoolOk(v.b) else BoolThrew
}

datatype StrsResult = Strs(ss: seq<Option<CsString>>) | StrsThrew

// items.Select(group => group.GetString()!), materialised by the collection expression.
function SelectGetString(items: seq<Json>): StrsResult
  decreases |items|
{
  if |items| == 0 then Strs([])
  else match GetString(items[0])
    case StrThrew => StrsThrew
    case Str(s) =>
      match SelectGetString(items[1..])
      case StrsThrew => StrsThrew
      case Strs(rest) => Strs([s] + rest)
}

// int.ToString() of a value (decimal, '-' for negatives; only n > 0 is rendered here).
function DigitChar(d: nat): char
  requires d < 10
{
  ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'][d]
}

function NatToDecimal(n: nat): seq<char>
  decreases n
{
  if n < 10 then [DigitChar(n)] else NatToDecimal(n / 10) + [DigitChar(n % 10)]
}

function Int32ToString(n: int32): seq<char>
{
  if n < 0 then "-" + NatToDecimal(-(n as int)) else NatToDecimal(n as int)
}

// ---------------------------------------------------------------------------
// Spec helpers
// ---------------------------------------------------------------------------

function FirstPage(t: CsString): seq<char>
{
  "/api/v3/core/users/?attributes=" + TenantFilter(t)
}

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

datatype PkResult = PkOk(pk: Option<CsString>) | PkThrew

// IdpAdminClient.cs:376-382 ReadPk
function ReadPk(element: Json): PkResult
{
  match GetProperty(element, "pk")                         // 378
  case GetThrew => PkThrew
  case Got(pk) =>
    if pk.JNumber? then PkOk(Some(pk.raw))                 // 379-380 GetRawText
    else match GetString(pk)                               // 381 GetString()!
      case StrThrew => PkThrew
      case Str(s) => PkOk(s)
}

// IdpAdminClient.cs:385-390 TenantIdOf, the throwing half: TryGetProperty on a non-object.
predicate TenantIdOfThrows(user: Json)
{
  match TryGetProperty(user, "attributes")                 // 386
  case TGThrew => true
  case TGMiss => false
  case TGHit(attrs) => TryGetProperty(attrs, "tenant_id").TGThrew?   // 387
}

// IdpAdminClient.cs:385-390 TenantIdOf, the value when it does not throw (None = null).
function TenantIdOf(user: Json): Option<CsString>
{
  match TryGetProperty(user, "attributes")                 // 386
  case TGThrew => None
  case TGMiss => None
  case TGHit(attrs) =>
    match TryGetProperty(attrs, "tenant_id")               // 387
    case TGThrew => None
    case TGMiss => None
    case TGHit(tid) =>
      if tid.JString? then Some(tid.s) else None           // 388-390
}

datatype CollectResult = Collected(users: seq<IdpUser>, sources: seq<Json>) | CollectThrew

// IdpAdminClient.cs:166-176, the foreach over root.GetProperty("results").EnumerateArray().
function Collect(tenantId: CsString, items: seq<Json>, i: nat, matches: seq<IdpUser>, sources: seq<Json>): CollectResult
  requires i <= |items|
  decreases |items| - i
{
  if i == |items| then Collected(matches, sources)
  else
    var user := items[i];
    if TenantIdOfThrows(user) then CollectThrew                         // 168
    else if TenantIdOf(user) != Some(tenantId) then                     // 168 (null != tenantId)
      Collect(tenantId, items, i + 1, matches, sources)                 // 169 continue
    else
      match ReadPk(user)                                                // 172
      case PkThrew => CollectThrew
      case PkOk(id) =>
        match PropString(user, "username")                              // 173
        case StrThrew => CollectThrew
        case Str(username) =>
          match PropString(user, "email")                               // 174
          case StrThrew => CollectThrew
          case Str(email) =>
            match PropString(user, "uid")                               // 175
            case StrThrew => CollectThrew
            case Str(uid) =>
              Collect(tenantId, items, i + 1,
                matches + [IdpUser(id, username, email, uid)],          // 171
                sources + [user])
}

// IdpAdminClient.cs:157-203, the while loop; path None is `path is null`.
function Loop(server: Server, tenantId: CsString, filter: CsString, path: Option<seq<char>>,
              matches: seq<IdpUser>, sources: seq<Json>, paths: seq<seq<char>>, fuel: nat): ListResult
  decreases fuel
{
  if path.None? then Users(matches, sources, paths)                     // 157, 205
  else if fuel == 0 then Unfinished(paths)
  else
    var sent := paths + [path.value];
    var ans := Get(server, path.value);                                 // 159, 162-163
    if ans.Transport? then ListThrew(sent)
    else if !(200 <= ans.status <= 299) then ListThrew(sent)            // 160 EnsureSuccessStatusCode
    else if ans.body.None? then ListThrew(sent)                         // 163 ParseAsync
    else
      var root := ans.body.value;                                       // 164
      match GetProperty(root, "results")                                // 166
      case GetThrew => ListThrew(sent)
      case Got(results) =>
        if !results.JArray? then ListThrew(sent)                        // 166 EnumerateArray
        else
          match Collect(tenantId, results.items, 0, matches, sources)   // 166-176
          case CollectThrew => ListThrew(sent)
          case Collected(matches', sources') =>
            match TryGetProperty(root, "pagination")                    // 186
            case TGThrew => ListThrew(sent)
            case TGMiss => ListThrew(sent)                              // 187
            case TGHit(pagination) =>
              match TryGetProperty(pagination, "next")                  // 191
              case TGThrew => ListThrew(sent)
              case TGMiss => ListThrew(sent)                            // 192
              case TGHit(next) =>
                if !next.JNumber? then ListThrew(sent)                  // 191-192
                else match JsonGetInt32(next.raw)                       // 196
                  case None => ListThrew(sent)
                  case Some(nextPage) =>
                    if nextPage < 0 then ListThrew(sent)                // 197-198
                    else
                      var path' := if nextPage > 0                      // 202
                        then Some("/api/v3/core/users/?attributes=" + filter + "&page=" + Int32ToString(nextPage))
                        else None;
                      Loop(server, tenantId, filter, path', matches', sources', sent, fuel - 1)
}

// IdpAdminClient.cs:148-206 ListUsersByTenantAsync
function ListUsersByTenant(server: Server, tenantId: CsString, fuel: nat): ListResult
{
  var filter := TenantFilter(tenantId);                                 // 152-153
  var path := Some("/api/v3/core/users/?attributes=" + filter);         // 155
  Loop(server, tenantId, filter, path, [], [], [], fuel)                // 154, 157-205
}

// IdpAdminClient.cs:214-238 GetUserAsync
function GetUser(server: Server, userId: CsString): GetUserResult
{
  var ans := Get(server, "/api/v3/core/users/" + userId + "/");         // 217, 222-223
  if ans.Transport? then Threw
  else if ans.status == 404 then Null                                   // 218-219
  else if !(200 <= ans.status <= 299) then Threw                        // 220 EnsureSuccessWithBodyAsync
  else if ans.body.None? then Threw                                     // 223 ParseAsync
  else
    var user := ans.body.value;                                         // 224
    match ReadPk(user)                                                  // 226
    case PkThrew => Threw
    case PkOk(pk) =>
      if pk != Some(userId) then Null                                   // 227-228
      else
        match PropString(user, "username")                              // 232
        case StrThrew => Threw
        case Str(username) =>
          match PropString(user, "email")                               // 233
          case StrThrew => Threw
          case Str(email) =>
            match PropString(user, "uid")                               // 234
            case StrThrew => Threw
            case Str(uid) =>
              match PropBool(user, "is_active")                         // 235
              case BoolThrew => Threw
              case BoolOk(isActive) =>
                match GetProperty(user, "groups")                       // 236
                case GetThrew => Threw
                case Got(groups) =>
                  if !groups.JArray? then Threw                         // 236 EnumerateArray
                  else match SelectGetString(groups.items)              // 236 Select(GetString()!)
                    case StrsThrew => Threw
                    case Strs(gs) =>
                      if TenantIdOfThrows(user) then Threw              // 237
                      else Found(IdpUserRecord(pk.value, username, email, uid, isActive, gs, TenantIdOf(user)))  // 230-237
}

// ---------------------------------------------------------------------------
// Proof helpers
// ---------------------------------------------------------------------------

ghost predicate PagePath(t: CsString, p: seq<char>)
{
  p == FirstPage(t) || exists n: int32 :: n > 0 && p == FirstPage(t) + "&page=" + Int32ToString(n)
}

ghost predicate AllPagePaths(t: CsString, ps: seq<seq<char>>)
{
  forall j :: 0 <= j < |ps| ==> PagePath(t, ps[j])
}

ghost predicate AllOfTenant(t: CsString, ss: seq<Json>)
{
  forall j :: 0 <= j < |ss| ==> TenantIdOf(ss[j]) == Some(t)
}

lemma CollectKeepsTenant(tenantId: CsString, items: seq<Json>, i: nat, matches: seq<IdpUser>, sources: seq<Json>)
  requires i <= |items|
  requires AllOfTenant(tenantId, sources)
  ensures Collect(tenantId, items, i, matches, sources).Collected? ==>
          AllOfTenant(tenantId, Collect(tenantId, items, i, matches, sources).sources)
  decreases |items| - i
{
  if i < |items| {
    var user := items[i];
    if !TenantIdOfThrows(user) {
      if TenantIdOf(user) != Some(tenantId) {
        CollectKeepsTenant(tenantId, items, i + 1, matches, sources);
      } else {
        match ReadPk(user)
        case PkThrew =>
        case PkOk(id) =>
          match PropString(user, "username")
          case StrThrew =>
          case Str(username) =>
            match PropString(user, "email")
            case StrThrew =>
            case Str(email) =>
              match PropString(user, "uid")
              case StrThrew =>
              case Str(uid) =>
                assert AllOfTenant(tenantId, sources + [user]);
                CollectKeepsTenant(tenantId, items, i + 1, matches + [IdpUser(id, username, email, uid)], sources + [user]);
      }
    }
  }
}

lemma LoopInvariants(server: Server, tenantId: CsString, path: Option<seq<char>>,
                     matches: seq<IdpUser>, sources: seq<Json>, paths: seq<seq<char>>, fuel: nat)
  requires path.Some? ==> PagePath(tenantId, path.value)
  requires AllPagePaths(tenantId, paths)
  requires AllOfTenant(tenantId, sources)
  ensures AllPagePaths(tenantId, Loop(server, tenantId, TenantFilter(tenantId), path, matches, sources, paths, fuel).paths)
  ensures Loop(server, tenantId, TenantFilter(tenantId), path, matches, sources, paths, fuel).Users? ==>
          AllOfTenant(tenantId, Loop(server, tenantId, TenantFilter(tenantId), path, matches, sources, paths, fuel).sources)
  decreases fuel
{
  var filter := TenantFilter(tenantId);
  if path.Some? && fuel > 0 {
    var sent := paths + [path.value];
    assert AllPagePaths(tenantId, sent);
    var ans := Get(server, path.value);
    if !ans.Transport? && 200 <= ans.status <= 299 && ans.body.Some? {
      var root := ans.body.value;
      match GetProperty(root, "results")
      case GetThrew =>
      case Got(results) =>
        if results.JArray? {
          CollectKeepsTenant(tenantId, results.items, 0, matches, sources);
          match Collect(tenantId, results.items, 0, matches, sources)
          case CollectThrew =>
          case Collected(matches', sources') =>
            match TryGetProperty(root, "pagination")
            case TGThrew =>
            case TGMiss =>
            case TGHit(pagination) =>
              match TryGetProperty(pagination, "next")
              case TGThrew =>
              case TGMiss =>
              case TGHit(next) =>
                if next.JNumber? {
                  match JsonGetInt32(next.raw)
                  case None =>
                  case Some(nextPage) =>
                    if nextPage >= 0 {
                      var path' := if nextPage > 0
                        then Some("/api/v3/core/users/?attributes=" + filter + "&page=" + Int32ToString(nextPage))
                        else None;
                      if nextPage > 0 {
                        assert path'.value == FirstPage(tenantId) + "&page=" + Int32ToString(nextPage);
                        assert PagePath(tenantId, path'.value);
                      }
                      LoopInvariants(server, tenantId, path', matches', sources', sent, fuel - 1);
                    }
                }
        }
    }
  }
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "`ListUsersByTenantAsync` requests `GET /api/v3/core/users/?attributes=<JSON {"tenant_id": t}>` on **every** page." (§2.1 line 38); test `ListUsersByTenantAsync_AsksAuthentikForTheTenantsUsersOnly` (T1 S1 1b), generalised
lemma P1_EveryPageCarriesTheFilter(server: Server, tenantId: CsString, fuel: nat, i: nat)
  requires i < |ListUsersByTenant(server, tenantId, fuel).paths|
  ensures ListUsersByTenant(server, tenantId, fuel).paths[i] == FirstPage(tenantId)
       || exists n: int32 :: n > 0 && ListUsersByTenant(server, tenantId, fuel).paths[i] == FirstPage(tenantId) + "&page=" + Int32ToString(n)
{
  var filter := TenantFilter(tenantId);
  var path := Some("/api/v3/core/users/?attributes=" + filter);
  assert path.value == FirstPage(tenantId);
  LoopInvariants(server, tenantId, path, [], [], [], fuel);
  assert PagePath(tenantId, ListUsersByTenant(server, tenantId, fuel).paths[i]);
}

// Citation: "The client keeps its own check that each returned user's `attributes.tenant_id` equals the tenant, because Authentik returns the unfiltered list when filtering raises a `ValueError`" (§2.1 line 39); test `ListUsersByTenantAsync_DropsAUserOfAnotherTenant_EvenWhenAuthentikReturnsIt`
lemma P2_OnlyTheTenantsUsersReturned(server: Server, tenantId: CsString, fuel: nat, i: nat)
  requires ListUsersByTenant(server, tenantId, fuel).Users?
  requires i < |ListUsersByTenant(server, tenantId, fuel).sources|
  ensures TenantIdOf(ListUsersByTenant(server, tenantId, fuel).sources[i]) == Some(tenantId)
{
  var filter := TenantFilter(tenantId);
  var path := Some("/api/v3/core/users/?attributes=" + filter);
  assert path.value == FirstPage(tenantId);
  LoopInvariants(server, tenantId, path, [], [], [], fuel);
}

// Citation: "404 maps to "not in this tenant"." (§2.1 line 40); test `GetUserAsync_NotFound_ReturnsNull`
lemma P3_NotFoundIsNull(server: Server, userId: CsString)
  requires Get(server, "/api/v3/core/users/" + userId + "/").Response?
  requires Get(server, "/api/v3/core/users/" + userId + "/").status == 404
  ensures GetUser(server, userId) == Null
{
}

// Citation: "`GetUserAsync` also requires the returned `pk` to equal the requested pk." (§2.1 line 42); test `GetUserAsync_ReturnedPkDiffersFromTheRequestedOne_ReturnsNull`
lemma P4_ReturnedRecordHasTheRequestedPk(server: Server, userId: CsString)
  requires GetUser(server, userId).Found?
  ensures GetUser(server, userId).record.Id == userId
{
}

// Citation: test `GetUserAsync_OtherFailure_ThrowsHttpRequestException` (T1 S1 1e), generalised
lemma P5_OtherFailureThrows(server: Server, userId: CsString)
  requires Get(server, "/api/v3/core/users/" + userId + "/").Response?
  requires Get(server, "/api/v3/core/users/" + userId + "/").status != 404
  requires !(200 <= Get(server, "/api/v3/core/users/" + userId + "/").status <= 299)
  ensures GetUser(server, userId) == Threw
{
}
