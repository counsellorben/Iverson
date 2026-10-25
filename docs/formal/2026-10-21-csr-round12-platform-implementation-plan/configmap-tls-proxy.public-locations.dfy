// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T8 S3
// Source: Iverson.Server/deploy/helm/iverson/charts/authentik/templates/configmap-tls-proxy.yaml the :9080 server block's locations (72–115)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/configmap-tls-proxy.public-locations.dfy

// ---------------------------------------------------------------------------------------------
// Vocabulary. nginx strings are byte sequences; every path constant is a named byte literal
// (the comment above each gives its text).
// ---------------------------------------------------------------------------------------------

newtype Byte = x: int | 0 <= x < 256

datatype Outcome = Forward | Redirect | NotFound

// '/'
const Root: seq<Byte> := [47]
// '/setup'
const Setup: seq<Byte> := [47, 115, 101, 116, 117, 112]
// '/if/flow/'
const IfFlow: seq<Byte> := [47, 105, 102, 47, 102, 108, 111, 119, 47]
// '/api/v3/flows/executor/'
const Executor: seq<Byte> := [47, 97, 112, 105, 47, 118, 51, 47, 102, 108, 111, 119, 115, 47, 101, 120, 101, 99, 117, 116, 111, 114, 47]
// 'initial-setup/'
const InitialSetup: seq<Byte> := [105, 110, 105, 116, 105, 97, 108, 45, 115, 101, 116, 117, 112, 47]
// '/if/user/'
const IfUser: seq<Byte> := [47, 105, 102, 47, 117, 115, 101, 114, 47]
// '/if/admin/'
const IfAdmin: seq<Byte> := [47, 105, 102, 47, 97, 100, 109, 105, 110, 47]
// '/api/'
const ApiPrefix: seq<Byte> := [47, 97, 112, 105, 47]
// '/'
const Slash: seq<Byte> := [47]
// '/ws/client/'
const WsClient: seq<Byte> := [47, 119, 115, 47, 99, 108, 105, 101, 110, 116, 47]
// '/-/health/live/'
const HealthLive: seq<Byte> := [47, 45, 47, 104, 101, 97, 108, 116, 104, 47, 108, 105, 118, 101, 47]
// '/-/health/ready/'
const HealthReady: seq<Byte> := [47, 45, 47, 104, 101, 97, 108, 116, 104, 47, 114, 101, 97, 100, 121, 47]
// '/static/'
const StaticPrefix: seq<Byte> := [47, 115, 116, 97, 116, 105, 99, 47]
// '/media/'
const MediaPrefix: seq<Byte> := [47, 109, 101, 100, 105, 97, 47]
// '/flows/-/default/authentication/'
const FlowsDefaultAuthentication: seq<Byte> := [47, 102, 108, 111, 119, 115, 47, 45, 47, 100, 101, 102, 97, 117, 108, 116, 47, 97, 117, 116, 104, 101, 110, 116, 105, 99, 97, 116, 105, 111, 110, 47]
// '/flows/-/default/invalidation/'
const FlowsDefaultInvalidation: seq<Byte> := [47, 102, 108, 111, 119, 115, 47, 45, 47, 100, 101, 102, 97, 117, 108, 116, 47, 105, 110, 118, 97, 108, 105, 100, 97, 116, 105, 111, 110, 47]
// '/flows/-/cancel/'
const FlowsCancel: seq<Byte> := [47, 102, 108, 111, 119, 115, 47, 45, 47, 99, 97, 110, 99, 101, 108, 47]
// '/application/o/authorize/'
const OAuthAuthorize: seq<Byte> := [47, 97, 112, 112, 108, 105, 99, 97, 116, 105, 111, 110, 47, 111, 47, 97, 117, 116, 104, 111, 114, 105, 122, 101, 47]
// '/application/o/token/'
const OAuthToken: seq<Byte> := [47, 97, 112, 112, 108, 105, 99, 97, 116, 105, 111, 110, 47, 111, 47, 116, 111, 107, 101, 110, 47]
// '/application/o/userinfo/'
const OAuthUserinfo: seq<Byte> := [47, 97, 112, 112, 108, 105, 99, 97, 116, 105, 111, 110, 47, 111, 47, 117, 115, 101, 114, 105, 110, 102, 111, 47]
// '/application/o/revoke/'
const OAuthRevoke: seq<Byte> := [47, 97, 112, 112, 108, 105, 99, 97, 116, 105, 111, 110, 47, 111, 47, 114, 101, 118, 111, 107, 101, 47]
// '/application/o/iverson-api/end-session/'
const OAuthEndSession: seq<Byte> := [47, 97, 112, 112, 108, 105, 99, 97, 116, 105, 111, 110, 47, 111, 47, 105, 118, 101, 114, 115, 111, 110, 45, 97, 112, 105, 47, 101, 110, 100, 45, 115, 101, 115, 115, 105, 111, 110, 47]
// '/application/o/iverson-api/jwks/'
const OAuthJwks: seq<Byte> := [47, 97, 112, 112, 108, 105, 99, 97, 116, 105, 111, 110, 47, 111, 47, 105, 118, 101, 114, 115, 111, 110, 45, 97, 112, 105, 47, 106, 119, 107, 115, 47]
// '/application/o/iverson-api/.well-known/openid-configuration'
const OAuthDiscovery: seq<Byte> := [47, 97, 112, 112, 108, 105, 99, 97, 116, 105, 111, 110, 47, 111, 47, 105, 118, 101, 114, 115, 111, 110, 45, 97, 112, 105, 47, 46, 119, 101, 108, 108, 45, 107, 110, 111, 119, 110, 47, 111, 112, 101, 110, 105, 100, 45, 99, 111, 110, 102, 105, 103, 117, 114, 97, 116, 105, 111, 110]
// 'default-authentication-flow'
const SlugDefaultAuthenticationFlow: seq<Byte> := [100, 101, 102, 97, 117, 108, 116, 45, 97, 117, 116, 104, 101, 110, 116, 105, 99, 97, 116, 105, 111, 110, 45, 102, 108, 111, 119]
// 'iverson-recovery'
const SlugIversonRecovery: seq<Byte> := [105, 118, 101, 114, 115, 111, 110, 45, 114, 101, 99, 111, 118, 101, 114, 121]
// 'default-provider-authorization-implicit-consent'
// (written as "default-" + the rest, so the solver can index it)
const SlugImplicitConsent: seq<Byte> := [100, 101, 102, 97, 117, 108, 116, 45] + [112, 114, 111, 118, 105, 100, 101, 114, 45, 97, 117, 116, 104, 111, 114, 105, 122, 97, 116, 105, 111, 110, 45, 105, 109, 112, 108, 105, 99, 105, 116, 45, 99, 111, 110, 115, 101, 110, 116]
// 'default-invalidation-flow'
const SlugDefaultInvalidationFlow: seq<Byte> := [100, 101, 102, 97, 117, 108, 116, 45, 105, 110, 118, 97, 108, 105, 100, 97, 116, 105, 111, 110, 45, 102, 108, 111, 119]
// 'default-provider-invalidation-flow'
const SlugProviderInvalidationFlow: seq<Byte> := [100, 101, 102, 97, 117, 108, 116, 45, 112, 114, 111, 118, 105, 100, 101, 114, 45, 105, 110, 118, 97, 108, 105, 100, 97, 116, 105, 111, 110, 45, 102, 108, 111, 119]

// The five slugs of design §2.7 lines 205–208, as written in the regex at yaml:89.
function AllowedSlugs(): set<seq<Byte>>
{
  {SlugDefaultAuthenticationFlow, SlugIversonRecovery, SlugImplicitConsent,
   SlugDefaultInvalidationFlow, SlugProviderInvalidationFlow}
}

// ---------------------------------------------------------------------------------------------
// Library boundary: the one regex location (yaml:89),
//   ~ ^/(if/flow|api/v3/flows/executor)/(<five slugs>)/\z
// axiomatised with PCRE's `\z`, which matches only at the very end of the subject.
// ---------------------------------------------------------------------------------------------

predicate FlowRegexMatches(uri: seq<Byte>)
  ensures FlowRegexMatches(uri) <==> exists a, s :: (a == IfFlow || a == Executor) && s in AllowedSlugs() && uri == a + s + Slash

// ---------------------------------------------------------------------------------------------
// Transliteration of the :9080 server block's locations (yaml:89–114). nginx selects a location
// for the normalised URI as follows: an exact (`=`) location that equals the URI wins outright;
// otherwise the longest matching prefix location is remembered, the regex locations are tried in
// file order (the first match wins), and if none matches the remembered prefix location is used.
// ---------------------------------------------------------------------------------------------

predicate StartsWith(s: seq<Byte>, p: seq<Byte>)
{
  |p| <= |s| && s[..|p|] == p
}

// Phase 2: the longest matching prefix location among /static/ (yaml:93), /media/ (yaml:94)
// and / (yaml:114). /static/ and /media/ cannot both match, and each is longer than /.
function LongestPrefixOutcome(uri: seq<Byte>): Outcome
{
  if StartsWith(uri, StaticPrefix) then Forward        // location /static/ { proxy_pass … }
  else if StartsWith(uri, MediaPrefix) then Forward    // location /media/ { proxy_pass … }
  else if StartsWith(uri, Root) then NotFound          // location / { return 404; }
  else NotFound                                        // no location matches (not reachable for a normalised URI)
}

function Route(uri: seq<Byte>): Outcome
{
  // Phase 1: exact locations, in file order.
  if uri == FlowsDefaultAuthentication then Forward          // yaml:90
  else if uri == FlowsDefaultInvalidation then Forward       // yaml:91
  else if uri == FlowsCancel then Forward                    // yaml:92
  else if uri == WsClient then Forward                       // yaml:95–104
  else if uri == OAuthAuthorize then Forward                 // yaml:105
  else if uri == OAuthToken then Forward                     // yaml:106
  else if uri == OAuthUserinfo then Forward                  // yaml:107
  else if uri == OAuthRevoke then Forward                    // yaml:108
  else if uri == OAuthEndSession then Forward                // yaml:109
  else if uri == OAuthJwks then Forward                      // yaml:110
  else if uri == OAuthDiscovery then Forward                 // yaml:111
  else if uri == HealthLive then Forward                     // yaml:112
  else if uri == Root then Redirect                          // yaml:113  return 302 <console>
  else
    // Phase 2: remember the longest matching prefix location.
    var remembered := LongestPrefixOutcome(uri);
    // Phase 3: the regex location (yaml:89), the block's only one.
    if FlowRegexMatches(uri) then Forward
    // Phase 4: fall back to the remembered prefix location.
    else remembered
}

// ---------------------------------------------------------------------------------------------
// Helpers.
// ---------------------------------------------------------------------------------------------

predicate IsExact(uri: seq<Byte>)
{
  uri == FlowsDefaultAuthentication || uri == FlowsDefaultInvalidation || uri == FlowsCancel
  || uri == WsClient || uri == OAuthAuthorize || uri == OAuthToken || uri == OAuthUserinfo
  || uri == OAuthRevoke || uri == OAuthEndSession || uri == OAuthJwks || uri == OAuthDiscovery
  || uri == HealthLive || uri == Root
}

lemma RouteNonExact(uri: seq<Byte>)
  requires !IsExact(uri)
  ensures Route(uri) == (if FlowRegexMatches(uri) then Forward else LongestPrefixOutcome(uri))
{
}

// Every allowed slug is non-empty, contains no '/', and has at least two bytes; its first two
// bytes are never "in" (105, 110), so none begins "initial-setup/".
lemma SlugFacts(s: seq<Byte>)
  requires s in AllowedSlugs()
  ensures |s| >= 2
  ensures !(s[0] == 105 && s[1] == 110)
{
  if s == SlugDefaultAuthenticationFlow {
    assert |s| == |SlugDefaultAuthenticationFlow| && s[0] == SlugDefaultAuthenticationFlow[0] == 100;
  } else if s == SlugIversonRecovery {
    assert s[1] == SlugIversonRecovery[1] == 118;
  } else if s == SlugImplicitConsent {
    assert |s| == |SlugImplicitConsent| && s[0] == SlugImplicitConsent[0] == 100;
  } else if s == SlugDefaultInvalidationFlow {
    assert |s| == |SlugDefaultInvalidationFlow| && s[0] == SlugDefaultInvalidationFlow[0] == 100;
  } else {
    assert s == SlugProviderInvalidationFlow;
    assert s[0] == SlugProviderInvalidationFlow[0] == 100;
  }
}

// The regex never matches a URI whose byte at index i (i < 9) differs from both IfFlow's and
// Executor's byte there.
lemma RegexNeedsHead(uri: seq<Byte>, i: nat)
  requires i < 9 && i < |uri|
  requires uri[i] != IfFlow[i] && uri[i] != Executor[i]
  ensures !FlowRegexMatches(uri)
{
  forall a, s | (a == IfFlow || a == Executor) && s in AllowedSlugs()
    ensures uri != a + s + Slash
  {
    assert (a + s + Slash)[i] == a[i];
  }
}

// The regex never matches a URI that starts with `a + InitialSetup` for a in {IfFlow, Executor}.
lemma RegexRefusesInitialSetup(a0: seq<Byte>, suffix: seq<Byte>)
  requires a0 == IfFlow || a0 == Executor
  ensures !FlowRegexMatches(a0 + InitialSetup + suffix)
{
  var uri := a0 + InitialSetup + suffix;
  forall a, s | (a == IfFlow || a == Executor) && s in AllowedSlugs()
    ensures uri != a + s + Slash
  {
    SlugFacts(s);
    var v := a + s + Slash;
    if a != a0 {
      assert a[1] != a0[1];
      assert v[1] == a[1] && uri[1] == a0[1];
    } else {
      var n := |a0|;
      assert uri[n] == 105 && uri[n + 1] == 110;
      assert v[n] == s[0] && v[n + 1] == s[1];
    }
  }
}

// Two sequences that differ at an index are unequal.
lemma NeqAt(x: seq<Byte>, y: seq<Byte>, i: nat)
  requires i < |x| && i < |y| && x[i] != y[i]
  ensures x != y
{
}

// No exact location begins "/i", so no URI beginning "/i" is an exact location.
lemma NotExactSlashI(uri: seq<Byte>)
  requires |uri| >= 2 && uri[1] == 105
  ensures !IsExact(uri)
{
  NeqAt(uri, FlowsDefaultAuthentication, 1); NeqAt(uri, FlowsDefaultInvalidation, 1);
  NeqAt(uri, FlowsCancel, 1); NeqAt(uri, WsClient, 1); NeqAt(uri, OAuthAuthorize, 1);
  NeqAt(uri, OAuthToken, 1); NeqAt(uri, OAuthUserinfo, 1); NeqAt(uri, OAuthRevoke, 1);
  NeqAt(uri, OAuthEndSession, 1); NeqAt(uri, OAuthJwks, 1); NeqAt(uri, OAuthDiscovery, 1);
  NeqAt(uri, HealthLive, 1);
}

// No exact location begins "/a?i" ("/application/…" has 'p' at index 3).
lemma NotExactSlashApi(uri: seq<Byte>)
  requires |uri| >= 4 && uri[1] == 97 && uri[3] == 105
  ensures !IsExact(uri)
{
  NeqAt(uri, FlowsDefaultAuthentication, 1); NeqAt(uri, FlowsDefaultInvalidation, 1);
  NeqAt(uri, FlowsCancel, 1); NeqAt(uri, WsClient, 1); NeqAt(uri, OAuthAuthorize, 3);
  NeqAt(uri, OAuthToken, 3); NeqAt(uri, OAuthUserinfo, 3); NeqAt(uri, OAuthRevoke, 3);
  NeqAt(uri, OAuthEndSession, 3); NeqAt(uri, OAuthJwks, 3); NeqAt(uri, OAuthDiscovery, 3);
  NeqAt(uri, HealthLive, 1);
}

// A URI whose byte 1 is 'a' and that is no Executor + s + Slash (s allowed) never matches the regex.
lemma RegexOnApiIsExecutor(uri: seq<Byte>)
  requires |uri| >= 2 && uri[1] == 97
  requires !(exists s :: s in AllowedSlugs() && uri == Executor + s + Slash)
  ensures !FlowRegexMatches(uri)
{
  forall a, s | (a == IfFlow || a == Executor) && s in AllowedSlugs()
    ensures uri != a + s + Slash
  {
    if a == IfFlow {
      assert (a + s + Slash)[1] == 105;
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Properties.
// ---------------------------------------------------------------------------------------------

// Citation: design AllowList9080.P1_RootRedirects: "**`= /` redirects (302) to the console URL** instead of forwarding to Authentik's root." (§2.7 line 216)
lemma P1_RootRedirects()
  ensures Route(Root) == Redirect
{
}

// Citation: design AllowList9080.P2_SetupNotForwarded: "`:9080` forwards neither `/setup` nor the flow's slug" (§2.6 line 192); "**Refused:** `initial-setup`" (§2.7 line 217)
lemma P2_SetupNotForwarded(suffix: seq<Byte>)
  ensures Route(Setup) != Forward
       && Route(IfFlow + InitialSetup + suffix) != Forward
       && Route(Executor + InitialSetup + suffix) != Forward
{
  // /setup
  assert Setup[1] != IfFlow[1] && Setup[1] != Executor[1];
  RegexNeedsHead(Setup, 1);
  assert !IsExact(Setup);
  RouteNonExact(Setup);
  assert LongestPrefixOutcome(Setup) == NotFound;

  var u1 := IfFlow + InitialSetup + suffix;
  assert u1[1] == 105;
  NotExactSlashI(u1);
  RouteNonExact(u1);
  RegexRefusesInitialSetup(IfFlow, suffix);
  assert u1[1] != StaticPrefix[1] && u1[1] != MediaPrefix[1];
  assert LongestPrefixOutcome(u1) == NotFound;

  var u2 := Executor + InitialSetup + suffix;
  assert u2[1] == 97 && u2[3] == 105;
  NotExactSlashApi(u2);
  RouteNonExact(u2);
  RegexRefusesInitialSetup(Executor, suffix);
  assert u2[1] != StaticPrefix[1] && u2[1] != MediaPrefix[1];
  assert LongestPrefixOutcome(u2) == NotFound;
}

// Citation: design AllowList9080.P3_InterfacesRefused: "**Refused:** … `/if/user/`, `/if/admin/` and the rest of `/api/`." (§2.7 line 217)
lemma P3_InterfacesRefused(suffix: seq<Byte>)
  ensures Route(IfUser + suffix) == NotFound && Route(IfAdmin + suffix) == NotFound
{
  var u1 := IfUser + suffix;
  assert u1[1] == 105 && u1[4] == 117;
  NotExactSlashI(u1);
  RouteNonExact(u1);
  RegexNeedsHead(u1, 4);
  assert u1[1] != StaticPrefix[1] && u1[1] != MediaPrefix[1];
  assert u1[..1] == Root;
  assert LongestPrefixOutcome(u1) == NotFound;

  var u2 := IfAdmin + suffix;
  assert u2[1] == 105 && u2[4] == 97;
  NotExactSlashI(u2);
  RouteNonExact(u2);
  RegexNeedsHead(u2, 4);
  assert u2[1] != StaticPrefix[1] && u2[1] != MediaPrefix[1];
  assert u2[..1] == Root;
  assert LongestPrefixOutcome(u2) == NotFound;
}

// Citation: design AllowList9080.P4_AllowedFlowsForwarded: "`/if/flow/<slug>/` and `/api/v3/flows/executor/<slug>/` for exactly: `default-authentication-flow`, `iverson-recovery`; `default-provider-authorization-implicit-consent` …; `default-invalidation-flow` …; `default-provider-invalidation-flow`" (§2.7 lines 204–208)
lemma P4_AllowedFlowsForwarded(slug: seq<Byte>)
  requires slug in AllowedSlugs()
  ensures Route(IfFlow + slug + Slash) == Forward && Route(Executor + slug + Slash) == Forward
{
  var u1 := IfFlow + slug + Slash;
  assert (IfFlow == IfFlow || IfFlow == Executor) && slug in AllowedSlugs() && u1 == IfFlow + slug + Slash;
  assert FlowRegexMatches(u1);
  assert u1 != Root by { assert |u1| > 1; }

  var u2 := Executor + slug + Slash;
  assert (Executor == IfFlow || Executor == Executor) && slug in AllowedSlugs() && u2 == Executor + slug + Slash;
  assert FlowRegexMatches(u2);
  assert u2 != Root by { assert |u2| > 1; }
}

// Citation: design AllowList9080.P5_OtherApiRefused: "the rest of `/api/`" refused (§2.7 line 217)
lemma P5_OtherApiRefused(path: seq<Byte>)
  requires |path| >= 5 && path[..5] == ApiPrefix
  requires !(exists s :: s in AllowedSlugs() && path == Executor + s + Slash)
  ensures Route(path) != Forward
{
  assert path[1] == 97 && path[3] == 105;
  NotExactSlashApi(path);
  RouteNonExact(path);
  assert path[1] != StaticPrefix[1] && path[1] != MediaPrefix[1];
  assert LongestPrefixOutcome(path) == NotFound;
  // Regex case: a match is IfFlow + s + Slash (excluded by path[1] == 'a') or
  // Executor + s + Slash (excluded by the second precondition).
  RegexOnApiIsExecutor(path);
}

// Citation: "`/ws/client/`, with WebSocket upgrade headers." (§2.7 line 211); "`= /-/health/live/`, for the GCP health check." (line 213); plan check `check_t8.py` (T8 S1: `/ws/client/` and `/-/health/live/` in `allowed`, `/-/health/ready/` in `refused`)
lemma P6_WebSocketAndLiveForwarded()
  ensures Route(WsClient) == Forward && Route(HealthLive) == Forward && Route(HealthReady) == NotFound
{
  assert HealthReady != HealthLive by { assert HealthReady[10] != HealthLive[10]; }
  assert !IsExact(HealthReady);
  RouteNonExact(HealthReady);
  RegexNeedsHead(HealthReady, 1);
  assert LongestPrefixOutcome(HealthReady) == NotFound;
}
