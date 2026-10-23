// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T23 S3
// Source: Iverson.Server/Iverson.ClientConformance/HarnessChannel.cs CheckEndpoints (20–21), Create (28–37); with the unchanged Iverson.LoadTest/Auth/CredentialEndpoints.cs Check (24–53)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/HarnessChannel.Create.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// C# string: a sequence of UTF-16 code units (--unicode-char false), Length an Int32.
type CsString = s: seq<char> | |s| < 0x8000_0000

// C# int.
newtype Int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

predicate IsAscii(s: CsString)
{
  forall i :: 0 <= i < |s| ==> (s[i] as int) < 128
}

function AsciiLowerChar(c: char): char
{
  if 'A' <= c <= 'Z' then ((c as int) + 32) as char else c
}

function AsciiLower(s: CsString): CsString
{
  seq(|s|, i requires 0 <= i < |s| => AsciiLowerChar(s[i]))
}

// ---------------------------------------------------------------------------------------------
// Library boundary
// ---------------------------------------------------------------------------------------------

// Approved axiom: string.StartsWith(prefix, StringComparison.OrdinalIgnoreCase).
function StartsWithOrdinalIgnoreCase(s: CsString, prefix: CsString): bool
  ensures IsAscii(s) && IsAscii(prefix) ==> (StartsWithOrdinalIgnoreCase(s, prefix) <==> |s| >= |prefix| && AsciiLower(s[..|prefix|]) == AsciiLower(prefix))

// Model vocabulary for a parsed absolute System.Uri: its (lower-cased) Scheme and IsLoopback.
datatype UriInfo = UriInfo(scheme: CsString, isLoopback: bool)

// Approved axiom: Uri.TryCreate(s, UriKind.Absolute, out uri) — Some(uri) when it returns true.
function UriParse(s: CsString): Option<UriInfo>

// Uri.UriSchemeHttps.
const UriSchemeHttps: CsString := "https"

// string.EndsWith(value, StringComparison.Ordinal) — modelled.
predicate EndsWithOrdinal(s: CsString, value: CsString)
{
  |s| >= |value| && s[|s| - |value|..] == value
}

// string.IndexOf(value, StringComparison.Ordinal) — modelled: the first index at which value
// occurs, or -1.
function IndexOfOrdinalFrom(s: CsString, value: CsString, i: nat): Int32
  requires i <= |s|
  decreases |s| - i
{
  if i + |value| > |s| then -1
  else if s[i..i + |value|] == value then i as Int32
  else if i == |s| then -1
  else IndexOfOrdinalFrom(s, value, i + 1)
}

function IndexOfOrdinal(s: CsString, value: CsString): Int32
{
  IndexOfOrdinalFrom(s, value, 0)
}

// The outcome of a C# expression that may throw.
datatype Outcome<T> = Returns(v: T) | Throws

// s[..k] (C# range): ArgumentOutOfRangeException unless 0 <= k <= s.Length.
function RangeTo(s: CsString, k: Int32): Outcome<CsString>
{
  if 0 <= k as int <= |s| then (var r: CsString := s[..k as int]; Returns(r)) else Throws
}

// Grpc.Net.Client: GrpcChannel.ForAddress(grpcUrl, options) with ChannelCredentials.Create(
// transport, callCredentials) is modelled as recording the options Create passes.
datatype Transport = SecureSsl | Insecure
datatype ChannelPlan = ChannelPlan(credentials: Transport, unsafeInsecureCallCredentials: bool)

// ---------------------------------------------------------------------------------------------
// Iverson.LoadTest/Auth/CredentialEndpoints.cs
// ---------------------------------------------------------------------------------------------

// The refusal messages (each literal's length is asserted so that it is a CsString).

const TokenEndpointPath: CsString := "/application/o/token/"
const ComposeAuthentikBaseUrl: CsString := "https://localhost:9443"

const MsgTokenEndpointShape: CsString := var m: string := "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/ (an Authentik token endpoint)."; assert |m| < 0x8000_0000; m
const MsgGrpcTls: CsString := var m: string := "IVERSON_GRPC_URL must be https:// or a loopback address: the service and acting-user tokens are sent on this channel."; assert |m| < 0x8000_0000; m
const MsgGrpcLoopback: CsString := var m: string := "IVERSON_GRPC_URL must be a loopback address: this harness's channel is plaintext and carries the service and acting-user tokens."; assert |m| < 0x8000_0000; m
const MsgTokenHttpsOrLoopback: CsString := var m: string := "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address: the client secret or assertion is sent to it."; assert |m| < 0x8000_0000; m
const MsgTokenHttpsForNetworkGrpc: CsString := var m: string := "IVERSON_TOKEN_ENDPOINT must be https:// when IVERSON_GRPC_URL is not a loopback address: the client library sends client credentials only to an https token endpoint on such a channel."; assert |m| < 0x8000_0000; m
const MsgAuthentikBase: CsString := var m: string := "The Authentik base URL (IVERSON_TOKEN_ENDPOINT without its path) must be https:// or a loopback address: user passwords and TOTP codes are sent to it."; assert |m| < 0x8000_0000; m

function AuthentikBaseUrl(tokenEndpoint: Option<CsString>): Outcome<CsString>
{
  if tokenEndpoint.Some? then RangeTo(tokenEndpoint.value, IndexOfOrdinal(tokenEndpoint.value, TokenEndpointPath))
  else Returns(ComposeAuthentikBaseUrl)
}

predicate IsHttpsOrLoopback(url: CsString)
{
  UriParse(url).Some? && (UriParse(url).value.scheme == UriSchemeHttps || UriParse(url).value.isLoopback)
}

function Check(grpcUrl: CsString, tokenEndpoint: Option<CsString>, grpcMayUseTls: bool): Option<CsString>
{
  if tokenEndpoint.Some?
     && !(UriParse(tokenEndpoint.value).Some?
          && EndsWithOrdinal(tokenEndpoint.value, TokenEndpointPath))
  then Some(MsgTokenEndpointShape)

  else if UriParse(grpcUrl).None?
     || !(UriParse(grpcUrl).value.isLoopback || (grpcMayUseTls && UriParse(grpcUrl).value.scheme == UriSchemeHttps))
  then (if grpcMayUseTls then Some(MsgGrpcTls) else Some(MsgGrpcLoopback))

  else if tokenEndpoint.Some? && !IsHttpsOrLoopback(tokenEndpoint.value)
  then Some(MsgTokenHttpsOrLoopback)

  else if grpcMayUseTls && !UriParse(grpcUrl).value.isLoopback && tokenEndpoint.Some?
     && !(UriParse(tokenEndpoint.value).Some? && UriParse(tokenEndpoint.value).value.scheme == UriSchemeHttps)
  then Some(MsgTokenHttpsForNetworkGrpc)

  else
    // AuthentikBaseUrl's range cannot throw here: the first branch passed, so a token endpoint
    // ends in TokenEndpointPath and IndexOf finds it (proved, not assumed).
    AuthentikBaseUrlReturnsAfterShapeCheck(tokenEndpoint);
    var baseUrl := AuthentikBaseUrl(tokenEndpoint);
    if !IsHttpsOrLoopback(baseUrl.v)
    then Some(MsgAuthentikBase)

    else None
}

// ---------------------------------------------------------------------------------------------
// Iverson.ClientConformance/HarnessChannel.cs
// ---------------------------------------------------------------------------------------------

function CheckEndpoints(grpcUrl: CsString, tokenEndpoint: Option<CsString>): Option<CsString>
{
  Check(grpcUrl, tokenEndpoint, true)
}

const HttpsPrefix: CsString := "https://"

// callCredentials is passed through to ChannelCredentials.Create unchanged and is not modelled.
function Create(grpcUrl: CsString): ChannelPlan
{
  var tls := StartsWithOrdinalIgnoreCase(grpcUrl, HttpsPrefix);
  ChannelPlan(
    unsafeInsecureCallCredentials := !tls,
    credentials := if tls then SecureSsl else Insecure)
}

predicate HasHttpsScheme(s: CsString)
{
  |s| >= 8 && AsciiLower(s[..8]) == "https://"
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------------------------

lemma IndexOfFromFindsMatch(s: CsString, value: CsString, i: nat, j: nat)
  requires i <= j && j + |value| <= |s| && s[j..j + |value|] == value
  ensures 0 <= IndexOfOrdinalFrom(s, value, i) as int <= j
  decreases |s| - i
{
  if i + |value| > |s| {
  } else if s[i..i + |value|] == value {
  } else if i == |s| {
  } else {
    assert i != j;
    IndexOfFromFindsMatch(s, value, i + 1, j);
  }
}

lemma AuthentikBaseUrlReturnsAfterShapeCheck(tokenEndpoint: Option<CsString>)
  requires tokenEndpoint.Some? ==> EndsWithOrdinal(tokenEndpoint.value, TokenEndpointPath)
  ensures AuthentikBaseUrl(tokenEndpoint).Returns?
{
  if tokenEndpoint.Some? {
    var s := tokenEndpoint.value;
    var j := |s| - |TokenEndpointPath|;
    assert s[j..j + |TokenEndpointPath|] == TokenEndpointPath;
    IndexOfFromFindsMatch(s, TokenEndpointPath, 0, j);
  }
}

lemma HttpsPrefixIsAsciiAndLower()
  ensures IsAscii(HttpsPrefix)
  ensures AsciiLower(HttpsPrefix) == HttpsPrefix
{
  assert |HttpsPrefix| == 8;
  assert forall i :: 0 <= i < 8 ==> !('A' <= HttpsPrefix[i] <= 'Z') && (HttpsPrefix[i] as int) < 128;
  assert AsciiLower(HttpsPrefix) == HttpsPrefix;
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------------------------

// Citation: "The harness (`ClientConformance/Program.cs:111-114`) and the five drivers (…) hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446); test `Create_BuildsAChannelWhoseTransportMatchesTheScheme` (`https://`, `HTTPS://`, `http://`)
lemma P1_TheSchemePicksTheTransport(grpcUrl: CsString)
  requires IsAscii(grpcUrl)
  ensures Create(grpcUrl).credentials == (if HasHttpsScheme(grpcUrl) then SecureSsl else Insecure)
       && Create(grpcUrl).unsafeInsecureCallCredentials == !HasHttpsScheme(grpcUrl)
{
  HttpsPrefixIsAsciiAndLower();
  assert StartsWithOrdinalIgnoreCase(grpcUrl, HttpsPrefix) <==> HasHttpsScheme(grpcUrl);
}

// Citation: test `CheckEndpoints_RefusesAPlaintextNetworkGrpcUrlOrTokenEndpoint` ("IVERSON_GRPC_URL must be https:// or a loopback address"), generalised
lemma P2_CredentialsGoOnlyToHttpsOrLoopback(grpcUrl: CsString, tokenEndpoint: Option<CsString>)
  requires CheckEndpoints(grpcUrl, tokenEndpoint).None?
  ensures UriParse(grpcUrl).Some? && (UriParse(grpcUrl).value.isLoopback || UriParse(grpcUrl).value.scheme == "https")
{
}
