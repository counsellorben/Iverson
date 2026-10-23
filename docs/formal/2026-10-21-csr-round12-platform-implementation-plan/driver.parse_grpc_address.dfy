// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T23 S4
// Source: Iverson.Clients/Python/conformance/driver.py parse_grpc_address (342–346), build_driver_channel (349–391)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/driver.parse_grpc_address.dfy

// ---- Shared modelling vocabulary ----
newtype CodePoint = x: int | 0 <= x < 0x110000
datatype Option<T> = None | Some(value: T)

// ---- Model vocabulary (library boundary) ----
datatype SplitResult = SplitResult(scheme: seq<CodePoint>, hostname: Option<seq<CodePoint>>, port: Option<int>, portRaises: bool)
datatype Parsed = Parsed(host: seq<CodePoint>, port: int, useTls: bool) | ParseRaised
datatype ChannelKind = SslChannel | InsecureChannel | LocalCompositeChannel | SslCompositeChannel

// "//"
const DoubleSlash: seq<CodePoint> := [47, 47]
// "https"
const HttpsScheme: seq<CodePoint> := [104, 116, 116, 112, 115]
// "localhost"
const Localhost: seq<CodePoint> := [108, 111, 99, 97, 108, 104, 111, 115, 116]

// Python `t in s` for str: substring containment.
predicate OccursAt(s: seq<CodePoint>, t: seq<CodePoint>, i: int)
{
  0 <= i && i + |t| <= |s| && s[i..i + |t|] == t
}

predicate Contains(s: seq<CodePoint>, t: seq<CodePoint>)
{
  exists i | 0 <= i <= |s| :: OccursAt(s, t, i)
}

// Python truthiness of Optional[str]: None and "" are falsy.
predicate Truthy(o: Option<seq<CodePoint>>)
{
  o.Some? && |o.value| > 0
}

// ---- Approved axiom: urllib.parse.urlsplit ----
function UrlSplit(s: seq<CodePoint>): SplitResult
  ensures |s| >= 2 && s[..2] == DoubleSlash ==> UrlSplit(s).scheme == []

// ---- Transliteration: parse_grpc_address (driver.py 342–346) ----
function ParseGrpcAddress(grpcAddr: seq<CodePoint>): Parsed
{
  // line 345: urlsplit(grpc_addr if "//" in grpc_addr else f"//{grpc_addr}")
  var parsed := UrlSplit(if Contains(grpcAddr, DoubleSlash) then grpcAddr else DoubleSlash + grpcAddr);
  // line 346: parsed.hostname or "localhost"  (None and "" are falsy)
  var host := if parsed.hostname.Some? && |parsed.hostname.value| > 0 then parsed.hostname.value else Localhost;
  // line 346: parsed.port raises ValueError on a malformed/out-of-range port
  if parsed.portRaises then ParseRaised
  else
    // line 346: parsed.port or 5000  (None and 0 are falsy)
    var port := if parsed.port.Some? && parsed.port.value != 0 then parsed.port.value else 5000;
    // line 346: parsed.scheme == "https"
    Parsed(host, port, parsed.scheme == HttpsScheme)
}

// ---- Transliteration: build_driver_channel (driver.py 349–391) ----
// Lines 369–381: how many call credentials the arguments produce.
function CallCredsCount(clientId: Option<seq<CodePoint>>, clientSecret: Option<seq<CodePoint>>,
                        tokenEndpoint: Option<seq<CodePoint>>, actingToken: Option<seq<CodePoint>>,
                        serviceToken: Option<seq<CodePoint>>): nat
{
  var first: nat :=
    if Truthy(serviceToken) then 1                                              // 370–373
    else if Truthy(clientId) && Truthy(clientSecret) && Truthy(tokenEndpoint) then 1  // 374–379
    else 0;
  first + (if Truthy(actingToken) then 1 else 0)                                // 380–381
}

// Lines 386–391, with call_creds abstracted to its length.
function BuildDriverChannel(useTls: bool, callCreds: nat): ChannelKind
{
  if callCreds == 0 then                                   // 386
    (if useTls then SslChannel else InsecureChannel)       // 387
  else
    // 389: base_creds = ssl if use_tls else local; 390–391: composite over base
    (if useTls then SslCompositeChannel else LocalCompositeChannel)
}

// Whole function: arguments -> channel kind.
function BuildDriverChannelFull(clientId: Option<seq<CodePoint>>, clientSecret: Option<seq<CodePoint>>,
                                tokenEndpoint: Option<seq<CodePoint>>, actingToken: Option<seq<CodePoint>>,
                                serviceToken: Option<seq<CodePoint>>, useTls: bool): ChannelKind
{
  BuildDriverChannel(useTls, CallCredsCount(clientId, clientSecret, tokenEndpoint, actingToken, serviceToken))
}

// ---- Helper ----
lemma PrefixDoubleSlash(addr: seq<CodePoint>)
  ensures |DoubleSlash + addr| >= 2 && (DoubleSlash + addr)[..2] == DoubleSlash
{
}

// ---- Properties ----

// Citation: test `test_parse_grpc_address_takes_tls_from_the_scheme` (`localhost:8080` → `False`), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446)
lemma P1_BareHostPortIsPlaintext(addr: seq<CodePoint>)
  requires !Contains(addr, DoubleSlash) && ParseGrpcAddress(addr).Parsed?
  ensures !ParseGrpcAddress(addr).useTls
{
  PrefixDoubleSlash(addr);
  assert UrlSplit(DoubleSlash + addr).scheme == [];
}

// Citation: test `test_parse_grpc_address_takes_tls_from_the_scheme` (`https://…` → `True`, `http://…` → `False`), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446)
lemma P2_HttpsSchemeIsTls(addr: seq<CodePoint>)
  requires Contains(addr, DoubleSlash) && ParseGrpcAddress(addr).Parsed?
  ensures ParseGrpcAddress(addr).useTls <==> UrlSplit(addr).scheme == HttpsScheme
{
}

// Citation: test `test_build_driver_channel_takes_its_transport_from_use_tls` (`(True, acting) → ssl`, `(False, acting) → local`, `(True, None) → ssl`, `(False, None) → []`), generalised
lemma P3_ChannelTransportFollowsUseTls(useTls: bool, callCreds: nat)
  ensures BuildDriverChannel(useTls, callCreds) == (if useTls then (if callCreds == 0 then SslChannel else SslCompositeChannel) else (if callCreds == 0 then InsecureChannel else LocalCompositeChannel))
{
}
