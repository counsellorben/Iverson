// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a11e848d722d05cc808840ae796e6afdb2101aa2
// Source: §2.7 ":9080 allow-list" (and §2.6 line 192), lines 203-217
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/AllowList9080.dfy
// Round: 3

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// What the :9080 public server block does with a request (§2.7 lines 203, 216).
datatype Outcome = Forward | Redirect | NotFound

// An nginx-style location. `= /x` (exact) is written with `=` in the spec
// (lines 213, 216); every other entry is a prefix location. The /ws/client/
// entry additionally needs WebSocket upgrade headers (line 211).
datatype Loc = Exact(p: string) | Prefix(p: string) | WsPrefix(p: string)

predicate IsPrefix(pre: string, s: string)
{
  |pre| <= |s| && s[..|pre|] == pre
}

// Line 204-208: the flow slugs, in the spec's order.
function AllowedSlugs(): seq<string>
{
  [ "default-authentication-flow",                      // line 205
    "iverson-recovery",                                 // line 205
    "default-provider-authorization-implicit-consent",  // line 206
    "default-invalidation-flow",                        // line 207
    "default-provider-invalidation-flow" ]              // line 208
}

// Lines 204-208: `/if/flow/<slug>/` and `/api/v3/flows/executor/<slug>/`
// for exactly the slugs above, in the spec's order.
function FlowLocs(): seq<Loc>
{
  [ Prefix("/if/flow/" + "default-authentication-flow" + "/"),                                  // line 205
    Prefix("/api/v3/flows/executor/" + "default-authentication-flow" + "/"),                    // line 205
    Prefix("/if/flow/" + "iverson-recovery" + "/"),                                             // line 205
    Prefix("/api/v3/flows/executor/" + "iverson-recovery" + "/"),                               // line 205
    Prefix("/if/flow/" + "default-provider-authorization-implicit-consent" + "/"),              // line 206
    Prefix("/api/v3/flows/executor/" + "default-provider-authorization-implicit-consent" + "/"),// line 206
    Prefix("/if/flow/" + "default-invalidation-flow" + "/"),                                    // line 207
    Prefix("/api/v3/flows/executor/" + "default-invalidation-flow" + "/"),                      // line 207
    Prefix("/if/flow/" + "default-provider-invalidation-flow" + "/"),                           // line 208
    Prefix("/api/v3/flows/executor/" + "default-provider-invalidation-flow" + "/") ]            // line 208
}

// Lines 209-213, in the spec's order.
function OtherLocs(): seq<Loc>
{
  [ Prefix("/flows/-/default/authentication/"),          // line 209
    Prefix("/flows/-/default/invalidation/"),            // line 209
    Prefix("/flows/-/cancel/"),                          // line 209
    Prefix("/static/"),                                  // line 210
    Prefix("/media/"),                                   // line 210
    WsPrefix("/ws/client/"),                             // line 211
    Prefix("/application/o/authorize/"),                 // line 212
    Prefix("/application/o/token/"),                     // line 212
    Prefix("/application/o/userinfo/"),                  // line 212
    Prefix("/application/o/revoke/"),                    // line 212
    Prefix("/application/o/iverson-api/end-session/"),   // line 212
    Prefix("/application/o/iverson-api/jwks/"),          // line 212
    Prefix("/application/o/iverson-api/.well-known/openid-configuration"), // line 212
    Exact("/-/health/live/") ]                           // line 213
}

// Line 203: "The public server block forwards only these".
function ForwardLocs(): seq<Loc>
{
  FlowLocs() + OtherLocs()
}

predicate Matches(l: Loc, path: string, wsUpgrade: bool)
{
  match l
  case Exact(p) => path == p
  case Prefix(p) => IsPrefix(p, path)
  case WsPrefix(p) => wsUpgrade && IsPrefix(p, path)
}

predicate AnyMatch(locs: seq<Loc>, path: string, wsUpgrade: bool)
{
  exists i :: 0 <= i < |locs| && Matches(locs[i], path, wsUpgrade)
}

// Lines 203-217: forward the allow-list (203-213), `= /` redirects (216),
// everything else (including the refused list, 217) answers 404 (203).
function RouteReq(path: string, wsUpgrade: bool): Outcome
{
  if AnyMatch(ForwardLocs(), path, wsUpgrade) then Forward
  else if path == "/" then Redirect
  else NotFound
}

// The properties name a path only. Route takes the most permissive header
// set (WebSocket upgrade headers present), so "not forwarded" results hold
// for every request on that path.
function Route(path: string): Outcome
{
  RouteReq(path, true)
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function Min(a: nat, b: nat): nat { if a <= b then a else b }

// p and q agree on their common length: then both could be prefixes of one path.
predicate Clash(p: string, q: string)
{
  p[..Min(|p|, |q|)] == q[..Min(|p|, |q|)]
}

// If p does not clash with a known prefix pre of path, the location at p
// cannot match path (exact or prefix).
lemma NoClashNoMatch(l: Loc, pre: string, path: string)
  requires IsPrefix(pre, path)
  requires !Clash(l.p, pre)
  ensures !Matches(l, path, true)
{
  var m := Min(|l.p|, |pre|);
  PrefixSlice(pre, path, m);
  PrefixSlice(l.p, path, m);
  assert path[..|path|] == path;
}

// A prefix of path agrees with path on its first m characters.
lemma PrefixSlice(p: string, path: string, m: nat)
  requires m <= |p|
  ensures IsPrefix(p, path) ==> p[..m] == path[..m]
{
  if IsPrefix(p, path) {
    assert p[..m] == path[..|p|][..m];
  }
}

lemma PrefixOfConcat(a: string, b: string)
  ensures IsPrefix(a, a + b)
{
  assert (a + b)[..|a|] == a;
}

// Dafny does not index string literals longer than about 45 characters
// directly; splitting this one exposes its first character.
lemma LongSlugHead()
  ensures "default-provider-authorization-implicit-consent"[0] == 'd'
{
  assert "default-provider-authorization-implicit-consent" == "default-provider-" + "authorization-implicit-consent";
}

// p and q differ at index k, inside both: they do not clash.
lemma DiffAt(p: string, q: string, k: nat)
  requires k < |p| && k < |q| && p[k] != q[k]
  ensures !Clash(p, q)
{
  var m := Min(|p|, |q|);
  assert p[..m][k] == p[k];
  assert q[..m][k] == q[k];
}

lemma NoClash_Setup()
  ensures forall i :: 0 <= i < |ForwardLocs()| ==> !Clash(ForwardLocs()[i].p, "/setup")
{
  forall i | 0 <= i < |ForwardLocs()|
    ensures !Clash(ForwardLocs()[i].p, "/setup")
  {
    if i == 0 { assert ForwardLocs()[0].p == "/if/flow/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[0].p, "/setup", 1);
    } else if i == 1 { assert ForwardLocs()[1].p == "/api/v3/flows/executor/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[1].p, "/setup", 1);
    } else if i == 2 { assert ForwardLocs()[2].p == "/if/flow/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[2].p, "/setup", 1);
    } else if i == 3 { assert ForwardLocs()[3].p == "/api/v3/flows/executor/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[3].p, "/setup", 1);
    } else if i == 4 { assert ForwardLocs()[4].p == "/if/flow/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[4].p, "/setup", 1);
    } else if i == 5 { assert ForwardLocs()[5].p == "/api/v3/flows/executor/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[5].p, "/setup", 1);
    } else if i == 6 { assert ForwardLocs()[6].p == "/if/flow/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[6].p, "/setup", 1);
    } else if i == 7 { assert ForwardLocs()[7].p == "/api/v3/flows/executor/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[7].p, "/setup", 1);
    } else if i == 8 { assert ForwardLocs()[8].p == "/if/flow/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[8].p, "/setup", 1);
    } else if i == 9 { assert ForwardLocs()[9].p == "/api/v3/flows/executor/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[9].p, "/setup", 1);
    } else if i == 10 { assert ForwardLocs()[10].p == "/flows/-/default/authentication/"; DiffAt(ForwardLocs()[10].p, "/setup", 1);
    } else if i == 11 { assert ForwardLocs()[11].p == "/flows/-/default/invalidation/"; DiffAt(ForwardLocs()[11].p, "/setup", 1);
    } else if i == 12 { assert ForwardLocs()[12].p == "/flows/-/cancel/"; DiffAt(ForwardLocs()[12].p, "/setup", 1);
    } else if i == 13 { assert ForwardLocs()[13].p == "/static/"; DiffAt(ForwardLocs()[13].p, "/setup", 2);
    } else if i == 14 { assert ForwardLocs()[14].p == "/media/"; DiffAt(ForwardLocs()[14].p, "/setup", 1);
    } else if i == 15 { assert ForwardLocs()[15].p == "/ws/client/"; DiffAt(ForwardLocs()[15].p, "/setup", 1);
    } else if i == 16 { assert ForwardLocs()[16].p == "/application/o/authorize/"; DiffAt(ForwardLocs()[16].p, "/setup", 1);
    } else if i == 17 { assert ForwardLocs()[17].p == "/application/o/token/"; DiffAt(ForwardLocs()[17].p, "/setup", 1);
    } else if i == 18 { assert ForwardLocs()[18].p == "/application/o/userinfo/"; DiffAt(ForwardLocs()[18].p, "/setup", 1);
    } else if i == 19 { assert ForwardLocs()[19].p == "/application/o/revoke/"; DiffAt(ForwardLocs()[19].p, "/setup", 1);
    } else if i == 20 { assert ForwardLocs()[20].p == "/application/o/iverson-api/end-session/"; DiffAt(ForwardLocs()[20].p, "/setup", 1);
    } else if i == 21 { assert ForwardLocs()[21].p == "/application/o/iverson-api/jwks/"; DiffAt(ForwardLocs()[21].p, "/setup", 1);
    } else if i == 22 { assert ForwardLocs()[22].p == "/application/o/iverson-api/.well-known/openid-configuration"; DiffAt(ForwardLocs()[22].p, "/setup", 1);
    } else if i == 23 { assert ForwardLocs()[23].p == "/-/health/live/"; DiffAt(ForwardLocs()[23].p, "/setup", 1);
    }
  }
}

lemma NoClash_IfFlowInitialSetup()
  ensures forall i :: 0 <= i < |ForwardLocs()| ==> !Clash(ForwardLocs()[i].p, "/if/flow/initial-setup/")
{
  forall i | 0 <= i < |ForwardLocs()|
    ensures !Clash(ForwardLocs()[i].p, "/if/flow/initial-setup/")
  {
    if i == 0 { assert ForwardLocs()[0].p == "/if/flow/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[0].p, "/if/flow/initial-setup/", 9);
    } else if i == 1 { assert ForwardLocs()[1].p == "/api/v3/flows/executor/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[1].p, "/if/flow/initial-setup/", 1);
    } else if i == 2 { assert ForwardLocs()[2].p == "/if/flow/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[2].p, "/if/flow/initial-setup/", 10);
    } else if i == 3 { assert ForwardLocs()[3].p == "/api/v3/flows/executor/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[3].p, "/if/flow/initial-setup/", 1);
    } else if i == 4 { assert ForwardLocs()[4].p == "/if/flow/" + "default-provider-authorization-implicit-consent" + "/"; LongSlugHead(); DiffAt(ForwardLocs()[4].p, "/if/flow/initial-setup/", 9);
    } else if i == 5 { assert ForwardLocs()[5].p == "/api/v3/flows/executor/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[5].p, "/if/flow/initial-setup/", 1);
    } else if i == 6 { assert ForwardLocs()[6].p == "/if/flow/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[6].p, "/if/flow/initial-setup/", 9);
    } else if i == 7 { assert ForwardLocs()[7].p == "/api/v3/flows/executor/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[7].p, "/if/flow/initial-setup/", 1);
    } else if i == 8 { assert ForwardLocs()[8].p == "/if/flow/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[8].p, "/if/flow/initial-setup/", 9);
    } else if i == 9 { assert ForwardLocs()[9].p == "/api/v3/flows/executor/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[9].p, "/if/flow/initial-setup/", 1);
    } else if i == 10 { assert ForwardLocs()[10].p == "/flows/-/default/authentication/"; DiffAt(ForwardLocs()[10].p, "/if/flow/initial-setup/", 1);
    } else if i == 11 { assert ForwardLocs()[11].p == "/flows/-/default/invalidation/"; DiffAt(ForwardLocs()[11].p, "/if/flow/initial-setup/", 1);
    } else if i == 12 { assert ForwardLocs()[12].p == "/flows/-/cancel/"; DiffAt(ForwardLocs()[12].p, "/if/flow/initial-setup/", 1);
    } else if i == 13 { assert ForwardLocs()[13].p == "/static/"; DiffAt(ForwardLocs()[13].p, "/if/flow/initial-setup/", 1);
    } else if i == 14 { assert ForwardLocs()[14].p == "/media/"; DiffAt(ForwardLocs()[14].p, "/if/flow/initial-setup/", 1);
    } else if i == 15 { assert ForwardLocs()[15].p == "/ws/client/"; DiffAt(ForwardLocs()[15].p, "/if/flow/initial-setup/", 1);
    } else if i == 16 { assert ForwardLocs()[16].p == "/application/o/authorize/"; DiffAt(ForwardLocs()[16].p, "/if/flow/initial-setup/", 1);
    } else if i == 17 { assert ForwardLocs()[17].p == "/application/o/token/"; DiffAt(ForwardLocs()[17].p, "/if/flow/initial-setup/", 1);
    } else if i == 18 { assert ForwardLocs()[18].p == "/application/o/userinfo/"; DiffAt(ForwardLocs()[18].p, "/if/flow/initial-setup/", 1);
    } else if i == 19 { assert ForwardLocs()[19].p == "/application/o/revoke/"; DiffAt(ForwardLocs()[19].p, "/if/flow/initial-setup/", 1);
    } else if i == 20 { assert ForwardLocs()[20].p == "/application/o/iverson-api/end-session/"; DiffAt(ForwardLocs()[20].p, "/if/flow/initial-setup/", 1);
    } else if i == 21 { assert ForwardLocs()[21].p == "/application/o/iverson-api/jwks/"; DiffAt(ForwardLocs()[21].p, "/if/flow/initial-setup/", 1);
    } else if i == 22 { assert ForwardLocs()[22].p == "/application/o/iverson-api/.well-known/openid-configuration"; DiffAt(ForwardLocs()[22].p, "/if/flow/initial-setup/", 1);
    } else if i == 23 { assert ForwardLocs()[23].p == "/-/health/live/"; DiffAt(ForwardLocs()[23].p, "/if/flow/initial-setup/", 1);
    }
  }
}

lemma NoClash_ApiInitialSetup()
  ensures forall i :: 0 <= i < |ForwardLocs()| ==> !Clash(ForwardLocs()[i].p, "/api/v3/flows/executor/initial-setup/")
{
  forall i | 0 <= i < |ForwardLocs()|
    ensures !Clash(ForwardLocs()[i].p, "/api/v3/flows/executor/initial-setup/")
  {
    if i == 0 { assert ForwardLocs()[0].p == "/if/flow/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[0].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 1 { assert ForwardLocs()[1].p == "/api/v3/flows/executor/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[1].p, "/api/v3/flows/executor/initial-setup/", 23);
    } else if i == 2 { assert ForwardLocs()[2].p == "/if/flow/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[2].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 3 { assert ForwardLocs()[3].p == "/api/v3/flows/executor/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[3].p, "/api/v3/flows/executor/initial-setup/", 24);
    } else if i == 4 { assert ForwardLocs()[4].p == "/if/flow/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[4].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 5 { assert ForwardLocs()[5].p == "/api/v3/flows/executor/" + "default-provider-authorization-implicit-consent" + "/"; LongSlugHead(); DiffAt(ForwardLocs()[5].p, "/api/v3/flows/executor/initial-setup/", 23);
    } else if i == 6 { assert ForwardLocs()[6].p == "/if/flow/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[6].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 7 { assert ForwardLocs()[7].p == "/api/v3/flows/executor/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[7].p, "/api/v3/flows/executor/initial-setup/", 23);
    } else if i == 8 { assert ForwardLocs()[8].p == "/if/flow/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[8].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 9 { assert ForwardLocs()[9].p == "/api/v3/flows/executor/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[9].p, "/api/v3/flows/executor/initial-setup/", 23);
    } else if i == 10 { assert ForwardLocs()[10].p == "/flows/-/default/authentication/"; DiffAt(ForwardLocs()[10].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 11 { assert ForwardLocs()[11].p == "/flows/-/default/invalidation/"; DiffAt(ForwardLocs()[11].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 12 { assert ForwardLocs()[12].p == "/flows/-/cancel/"; DiffAt(ForwardLocs()[12].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 13 { assert ForwardLocs()[13].p == "/static/"; DiffAt(ForwardLocs()[13].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 14 { assert ForwardLocs()[14].p == "/media/"; DiffAt(ForwardLocs()[14].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 15 { assert ForwardLocs()[15].p == "/ws/client/"; DiffAt(ForwardLocs()[15].p, "/api/v3/flows/executor/initial-setup/", 1);
    } else if i == 16 { assert ForwardLocs()[16].p == "/application/o/authorize/"; DiffAt(ForwardLocs()[16].p, "/api/v3/flows/executor/initial-setup/", 3);
    } else if i == 17 { assert ForwardLocs()[17].p == "/application/o/token/"; DiffAt(ForwardLocs()[17].p, "/api/v3/flows/executor/initial-setup/", 3);
    } else if i == 18 { assert ForwardLocs()[18].p == "/application/o/userinfo/"; DiffAt(ForwardLocs()[18].p, "/api/v3/flows/executor/initial-setup/", 3);
    } else if i == 19 { assert ForwardLocs()[19].p == "/application/o/revoke/"; DiffAt(ForwardLocs()[19].p, "/api/v3/flows/executor/initial-setup/", 3);
    } else if i == 20 { assert ForwardLocs()[20].p == "/application/o/iverson-api/end-session/"; DiffAt(ForwardLocs()[20].p, "/api/v3/flows/executor/initial-setup/", 3);
    } else if i == 21 { assert ForwardLocs()[21].p == "/application/o/iverson-api/jwks/"; DiffAt(ForwardLocs()[21].p, "/api/v3/flows/executor/initial-setup/", 3);
    } else if i == 22 { assert ForwardLocs()[22].p == "/application/o/iverson-api/.well-known/openid-configuration"; DiffAt(ForwardLocs()[22].p, "/api/v3/flows/executor/initial-setup/", 3);
    } else if i == 23 { assert ForwardLocs()[23].p == "/-/health/live/"; DiffAt(ForwardLocs()[23].p, "/api/v3/flows/executor/initial-setup/", 1);
    }
  }
}

lemma NoClash_IfUser()
  ensures forall i :: 0 <= i < |ForwardLocs()| ==> !Clash(ForwardLocs()[i].p, "/if/user/")
{
  forall i | 0 <= i < |ForwardLocs()|
    ensures !Clash(ForwardLocs()[i].p, "/if/user/")
  {
    if i == 0 { assert ForwardLocs()[0].p == "/if/flow/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[0].p, "/if/user/", 4);
    } else if i == 1 { assert ForwardLocs()[1].p == "/api/v3/flows/executor/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[1].p, "/if/user/", 1);
    } else if i == 2 { assert ForwardLocs()[2].p == "/if/flow/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[2].p, "/if/user/", 4);
    } else if i == 3 { assert ForwardLocs()[3].p == "/api/v3/flows/executor/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[3].p, "/if/user/", 1);
    } else if i == 4 { assert ForwardLocs()[4].p == "/if/flow/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[4].p, "/if/user/", 4);
    } else if i == 5 { assert ForwardLocs()[5].p == "/api/v3/flows/executor/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[5].p, "/if/user/", 1);
    } else if i == 6 { assert ForwardLocs()[6].p == "/if/flow/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[6].p, "/if/user/", 4);
    } else if i == 7 { assert ForwardLocs()[7].p == "/api/v3/flows/executor/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[7].p, "/if/user/", 1);
    } else if i == 8 { assert ForwardLocs()[8].p == "/if/flow/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[8].p, "/if/user/", 4);
    } else if i == 9 { assert ForwardLocs()[9].p == "/api/v3/flows/executor/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[9].p, "/if/user/", 1);
    } else if i == 10 { assert ForwardLocs()[10].p == "/flows/-/default/authentication/"; DiffAt(ForwardLocs()[10].p, "/if/user/", 1);
    } else if i == 11 { assert ForwardLocs()[11].p == "/flows/-/default/invalidation/"; DiffAt(ForwardLocs()[11].p, "/if/user/", 1);
    } else if i == 12 { assert ForwardLocs()[12].p == "/flows/-/cancel/"; DiffAt(ForwardLocs()[12].p, "/if/user/", 1);
    } else if i == 13 { assert ForwardLocs()[13].p == "/static/"; DiffAt(ForwardLocs()[13].p, "/if/user/", 1);
    } else if i == 14 { assert ForwardLocs()[14].p == "/media/"; DiffAt(ForwardLocs()[14].p, "/if/user/", 1);
    } else if i == 15 { assert ForwardLocs()[15].p == "/ws/client/"; DiffAt(ForwardLocs()[15].p, "/if/user/", 1);
    } else if i == 16 { assert ForwardLocs()[16].p == "/application/o/authorize/"; DiffAt(ForwardLocs()[16].p, "/if/user/", 1);
    } else if i == 17 { assert ForwardLocs()[17].p == "/application/o/token/"; DiffAt(ForwardLocs()[17].p, "/if/user/", 1);
    } else if i == 18 { assert ForwardLocs()[18].p == "/application/o/userinfo/"; DiffAt(ForwardLocs()[18].p, "/if/user/", 1);
    } else if i == 19 { assert ForwardLocs()[19].p == "/application/o/revoke/"; DiffAt(ForwardLocs()[19].p, "/if/user/", 1);
    } else if i == 20 { assert ForwardLocs()[20].p == "/application/o/iverson-api/end-session/"; DiffAt(ForwardLocs()[20].p, "/if/user/", 1);
    } else if i == 21 { assert ForwardLocs()[21].p == "/application/o/iverson-api/jwks/"; DiffAt(ForwardLocs()[21].p, "/if/user/", 1);
    } else if i == 22 { assert ForwardLocs()[22].p == "/application/o/iverson-api/.well-known/openid-configuration"; DiffAt(ForwardLocs()[22].p, "/if/user/", 1);
    } else if i == 23 { assert ForwardLocs()[23].p == "/-/health/live/"; DiffAt(ForwardLocs()[23].p, "/if/user/", 1);
    }
  }
}

lemma NoClash_IfAdmin()
  ensures forall i :: 0 <= i < |ForwardLocs()| ==> !Clash(ForwardLocs()[i].p, "/if/admin/")
{
  forall i | 0 <= i < |ForwardLocs()|
    ensures !Clash(ForwardLocs()[i].p, "/if/admin/")
  {
    if i == 0 { assert ForwardLocs()[0].p == "/if/flow/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[0].p, "/if/admin/", 4);
    } else if i == 1 { assert ForwardLocs()[1].p == "/api/v3/flows/executor/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[1].p, "/if/admin/", 1);
    } else if i == 2 { assert ForwardLocs()[2].p == "/if/flow/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[2].p, "/if/admin/", 4);
    } else if i == 3 { assert ForwardLocs()[3].p == "/api/v3/flows/executor/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[3].p, "/if/admin/", 1);
    } else if i == 4 { assert ForwardLocs()[4].p == "/if/flow/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[4].p, "/if/admin/", 4);
    } else if i == 5 { assert ForwardLocs()[5].p == "/api/v3/flows/executor/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[5].p, "/if/admin/", 1);
    } else if i == 6 { assert ForwardLocs()[6].p == "/if/flow/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[6].p, "/if/admin/", 4);
    } else if i == 7 { assert ForwardLocs()[7].p == "/api/v3/flows/executor/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[7].p, "/if/admin/", 1);
    } else if i == 8 { assert ForwardLocs()[8].p == "/if/flow/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[8].p, "/if/admin/", 4);
    } else if i == 9 { assert ForwardLocs()[9].p == "/api/v3/flows/executor/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[9].p, "/if/admin/", 1);
    } else if i == 10 { assert ForwardLocs()[10].p == "/flows/-/default/authentication/"; DiffAt(ForwardLocs()[10].p, "/if/admin/", 1);
    } else if i == 11 { assert ForwardLocs()[11].p == "/flows/-/default/invalidation/"; DiffAt(ForwardLocs()[11].p, "/if/admin/", 1);
    } else if i == 12 { assert ForwardLocs()[12].p == "/flows/-/cancel/"; DiffAt(ForwardLocs()[12].p, "/if/admin/", 1);
    } else if i == 13 { assert ForwardLocs()[13].p == "/static/"; DiffAt(ForwardLocs()[13].p, "/if/admin/", 1);
    } else if i == 14 { assert ForwardLocs()[14].p == "/media/"; DiffAt(ForwardLocs()[14].p, "/if/admin/", 1);
    } else if i == 15 { assert ForwardLocs()[15].p == "/ws/client/"; DiffAt(ForwardLocs()[15].p, "/if/admin/", 1);
    } else if i == 16 { assert ForwardLocs()[16].p == "/application/o/authorize/"; DiffAt(ForwardLocs()[16].p, "/if/admin/", 1);
    } else if i == 17 { assert ForwardLocs()[17].p == "/application/o/token/"; DiffAt(ForwardLocs()[17].p, "/if/admin/", 1);
    } else if i == 18 { assert ForwardLocs()[18].p == "/application/o/userinfo/"; DiffAt(ForwardLocs()[18].p, "/if/admin/", 1);
    } else if i == 19 { assert ForwardLocs()[19].p == "/application/o/revoke/"; DiffAt(ForwardLocs()[19].p, "/if/admin/", 1);
    } else if i == 20 { assert ForwardLocs()[20].p == "/application/o/iverson-api/end-session/"; DiffAt(ForwardLocs()[20].p, "/if/admin/", 1);
    } else if i == 21 { assert ForwardLocs()[21].p == "/application/o/iverson-api/jwks/"; DiffAt(ForwardLocs()[21].p, "/if/admin/", 1);
    } else if i == 22 { assert ForwardLocs()[22].p == "/application/o/iverson-api/.well-known/openid-configuration"; DiffAt(ForwardLocs()[22].p, "/if/admin/", 1);
    } else if i == 23 { assert ForwardLocs()[23].p == "/-/health/live/"; DiffAt(ForwardLocs()[23].p, "/if/admin/", 1);
    }
  }
}

lemma NoClash_Api()
  ensures forall i :: 0 <= i < |ForwardLocs()| && i !in {1, 3, 5, 7, 9} ==> !Clash(ForwardLocs()[i].p, "/api/")
{
  forall i | 0 <= i < |ForwardLocs()| && i !in {1, 3, 5, 7, 9}
    ensures !Clash(ForwardLocs()[i].p, "/api/")
  {
    if i == 0 { assert ForwardLocs()[0].p == "/if/flow/" + "default-authentication-flow" + "/"; DiffAt(ForwardLocs()[0].p, "/api/", 1);
    } else if i == 2 { assert ForwardLocs()[2].p == "/if/flow/" + "iverson-recovery" + "/"; DiffAt(ForwardLocs()[2].p, "/api/", 1);
    } else if i == 4 { assert ForwardLocs()[4].p == "/if/flow/" + "default-provider-authorization-implicit-consent" + "/"; DiffAt(ForwardLocs()[4].p, "/api/", 1);
    } else if i == 6 { assert ForwardLocs()[6].p == "/if/flow/" + "default-invalidation-flow" + "/"; DiffAt(ForwardLocs()[6].p, "/api/", 1);
    } else if i == 8 { assert ForwardLocs()[8].p == "/if/flow/" + "default-provider-invalidation-flow" + "/"; DiffAt(ForwardLocs()[8].p, "/api/", 1);
    } else if i == 10 { assert ForwardLocs()[10].p == "/flows/-/default/authentication/"; DiffAt(ForwardLocs()[10].p, "/api/", 1);
    } else if i == 11 { assert ForwardLocs()[11].p == "/flows/-/default/invalidation/"; DiffAt(ForwardLocs()[11].p, "/api/", 1);
    } else if i == 12 { assert ForwardLocs()[12].p == "/flows/-/cancel/"; DiffAt(ForwardLocs()[12].p, "/api/", 1);
    } else if i == 13 { assert ForwardLocs()[13].p == "/static/"; DiffAt(ForwardLocs()[13].p, "/api/", 1);
    } else if i == 14 { assert ForwardLocs()[14].p == "/media/"; DiffAt(ForwardLocs()[14].p, "/api/", 1);
    } else if i == 15 { assert ForwardLocs()[15].p == "/ws/client/"; DiffAt(ForwardLocs()[15].p, "/api/", 1);
    } else if i == 16 { assert ForwardLocs()[16].p == "/application/o/authorize/"; DiffAt(ForwardLocs()[16].p, "/api/", 3);
    } else if i == 17 { assert ForwardLocs()[17].p == "/application/o/token/"; DiffAt(ForwardLocs()[17].p, "/api/", 3);
    } else if i == 18 { assert ForwardLocs()[18].p == "/application/o/userinfo/"; DiffAt(ForwardLocs()[18].p, "/api/", 3);
    } else if i == 19 { assert ForwardLocs()[19].p == "/application/o/revoke/"; DiffAt(ForwardLocs()[19].p, "/api/", 3);
    } else if i == 20 { assert ForwardLocs()[20].p == "/application/o/iverson-api/end-session/"; DiffAt(ForwardLocs()[20].p, "/api/", 3);
    } else if i == 21 { assert ForwardLocs()[21].p == "/application/o/iverson-api/jwks/"; DiffAt(ForwardLocs()[21].p, "/api/", 3);
    } else if i == 22 { assert ForwardLocs()[22].p == "/application/o/iverson-api/.well-known/openid-configuration"; DiffAt(ForwardLocs()[22].p, "/api/", 3);
    } else if i == 23 { assert ForwardLocs()[23].p == "/-/health/live/"; DiffAt(ForwardLocs()[23].p, "/api/", 1);
    }
  }
}

// Line 204: entries 2j and 2j+1 are the two locations of the j-th slug.
lemma FlowEntries(j: nat)
  requires j < |AllowedSlugs()|
  ensures ForwardLocs()[2 * j] == Prefix("/if/flow/" + AllowedSlugs()[j] + "/")
  ensures ForwardLocs()[2 * j + 1] == Prefix("/api/v3/flows/executor/" + AllowedSlugs()[j] + "/")
{
  if j == 0 {
    assert "/if/flow/" + AllowedSlugs()[0] + "/" == "/if/flow/" + "default-authentication-flow" + "/";
    assert "/api/v3/flows/executor/" + AllowedSlugs()[0] + "/" == "/api/v3/flows/executor/" + "default-authentication-flow" + "/";
  } else if j == 1 {
    assert "/if/flow/" + AllowedSlugs()[1] + "/" == "/if/flow/" + "iverson-recovery" + "/";
    assert "/api/v3/flows/executor/" + AllowedSlugs()[1] + "/" == "/api/v3/flows/executor/" + "iverson-recovery" + "/";
  } else if j == 2 {
    assert "/if/flow/" + AllowedSlugs()[2] + "/" == "/if/flow/" + "default-provider-authorization-implicit-consent" + "/";
    assert "/api/v3/flows/executor/" + AllowedSlugs()[2] + "/" == "/api/v3/flows/executor/" + "default-provider-authorization-implicit-consent" + "/";
  } else if j == 3 {
    assert "/if/flow/" + AllowedSlugs()[3] + "/" == "/if/flow/" + "default-invalidation-flow" + "/";
    assert "/api/v3/flows/executor/" + AllowedSlugs()[3] + "/" == "/api/v3/flows/executor/" + "default-invalidation-flow" + "/";
  } else {
    assert "/if/flow/" + AllowedSlugs()[4] + "/" == "/if/flow/" + "default-provider-invalidation-flow" + "/";
    assert "/api/v3/flows/executor/" + AllowedSlugs()[4] + "/" == "/api/v3/flows/executor/" + "default-provider-invalidation-flow" + "/";
  }
}

lemma NotForwardedBy(path: string, pre: string)
  requires IsPrefix(pre, path)
  requires forall i :: 0 <= i < |ForwardLocs()| ==> !Clash(ForwardLocs()[i].p, pre)
  ensures Route(path) != Forward
  ensures path != "/" ==> Route(path) == NotFound
{
  forall i | 0 <= i < |ForwardLocs()|
    ensures !Matches(ForwardLocs()[i], path, true)
  {
    NoClashNoMatch(ForwardLocs()[i], pre, path);
  }
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "**`= /` redirects (302) to the console URL** instead of forwarding to Authentik's root." (§2.7 line 216)
lemma P1_RootRedirects()
  ensures Route("/") == Redirect
{
  forall i | 0 <= i < |ForwardLocs()|
    ensures !Matches(ForwardLocs()[i], "/", true)
  {
    assert |ForwardLocs()[i].p| > 1;
  }
}

// Citation: "`:9080` forwards neither `/setup` nor the flow's slug" (§2.6 line 192); "**Refused:** `initial-setup`" (§2.7 line 217)
lemma P2_SetupNotForwarded(suffix: string)
  ensures Route("/setup") != Forward
       && Route("/if/flow/initial-setup/" + suffix) != Forward
       && Route("/api/v3/flows/executor/initial-setup/" + suffix) != Forward
{
  PrefixOfConcat("/setup", "");
  assert "/setup" + "" == "/setup";
  NoClash_Setup();
  NotForwardedBy("/setup", "/setup");
  PrefixOfConcat("/if/flow/initial-setup/", suffix);
  NoClash_IfFlowInitialSetup();
  NotForwardedBy("/if/flow/initial-setup/" + suffix, "/if/flow/initial-setup/");
  PrefixOfConcat("/api/v3/flows/executor/initial-setup/", suffix);
  NoClash_ApiInitialSetup();
  NotForwardedBy("/api/v3/flows/executor/initial-setup/" + suffix, "/api/v3/flows/executor/initial-setup/");
}

// Citation: "**Refused:** … `/if/user/`, `/if/admin/` and the rest of `/api/`." (§2.7 line 217)
lemma P3_InterfacesRefused(suffix: string)
  ensures Route("/if/user/" + suffix) == NotFound && Route("/if/admin/" + suffix) == NotFound
{
  PrefixOfConcat("/if/user/", suffix);
  NoClash_IfUser();
  NotForwardedBy("/if/user/" + suffix, "/if/user/");
  PrefixOfConcat("/if/admin/", suffix);
  NoClash_IfAdmin();
  NotForwardedBy("/if/admin/" + suffix, "/if/admin/");
}

// Citation: "`/if/flow/<slug>/` and `/api/v3/flows/executor/<slug>/` for exactly: `default-authentication-flow`, `iverson-recovery`; `default-provider-authorization-implicit-consent` …; `default-invalidation-flow` …; `default-provider-invalidation-flow`" (§2.7 lines 204–208)
lemma P4_AllowedFlowsForwarded(slug: string)
  requires slug in AllowedSlugs()
  ensures Route("/if/flow/" + slug + "/") == Forward && Route("/api/v3/flows/executor/" + slug + "/") == Forward
{
  var a := "/if/flow/" + slug + "/";
  var b := "/api/v3/flows/executor/" + slug + "/";
  assert a[..|a|] == a;
  assert b[..|b|] == b;
  var j :| 0 <= j < |AllowedSlugs()| && AllowedSlugs()[j] == slug;
  FlowEntries(j);
  assert Matches(ForwardLocs()[2 * j], a, true);
  assert Matches(ForwardLocs()[2 * j + 1], b, true);
}

// Citation: "the rest of `/api/`" refused (§2.7 line 217)
lemma P5_OtherApiRefused(path: string)
  requires |path| >= 5 && path[..5] == "/api/"
  requires !(exists s :: s in AllowedSlugs() && IsPrefix("/api/v3/flows/executor/" + s + "/", path))
  ensures Route(path) != Forward
{
  assert path[..5] == "/api/";
  NoClash_Api();
  forall i | 0 <= i < |ForwardLocs()|
    ensures !Matches(ForwardLocs()[i], path, true)
  {
    if i < 10 && i % 2 == 1 {
      var j := i / 2;
      FlowEntries(j);
      var s := AllowedSlugs()[j];
      assert s in AllowedSlugs();
      assert 2 * j + 1 == i;
    } else {
      assert i !in {1, 3, 5, 7, 9};
      NoClashNoMatch(ForwardLocs()[i], "/api/", path);
    }
  }
}
