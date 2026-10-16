// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan.md, T3 S3
// Source: Iverson.Server/Iverson.LoadTest/Auth/CredentialEndpoints.cs Check, AuthentikBaseUrl, IsLoopback, IsHttpsOrLoopback
// Replay base: 7d4669e5896c4df448b2d45c9a4abbf67926b688
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --unicode-char false --allow-deprecation docs/formal/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan/CredentialEndpoints.Check.dfy

// ---------------------------------------------------------------------------
// Source-language types
// ---------------------------------------------------------------------------

// C# string: UTF-16 code units; Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

// C# int.
newtype Int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

// C# nullable reference.
datatype Option<T> = None | Some(value: T)

// Exceptions are outcomes.
datatype Exn = ArgumentOutOfRangeException
datatype Outcome<T> = Returned(value: T) | Threw(exn: Exn)

// ---------------------------------------------------------------------------
// Approved axioms (library boundary: System.Uri)
//   UriIsAbsolute(s)  == Uri.TryCreate(s, UriKind.Absolute, out _)
//   UriScheme(s)      == the parsed Uri's .Scheme   (meaningful only when UriIsAbsolute(s))
//   UriIsLoopback(s)  == the parsed Uri's .IsLoopback (meaningful only when UriIsAbsolute(s))
// ---------------------------------------------------------------------------

function UriIsAbsolute(s: CsString): bool

function UriScheme(s: CsString): CsString

function UriIsLoopback(s: CsString): bool

// Uri.UriSchemeHttps
const UriSchemeHttps: CsString := "https"

// ---------------------------------------------------------------------------
// Modelled BCL string operations (ordinal)
// ---------------------------------------------------------------------------

// string.EndsWith(value, StringComparison.Ordinal)
function EndsWithOrdinal(s: CsString, v: CsString): bool
{
  |s| >= |v| && s[|s| - |v|..] == v
}

// First ordinal match of v in s at or after i, or -1.
function IndexOfFrom(s: CsString, v: CsString, i: nat): (r: int)
  requires i <= |s|
  ensures r == -1 || (i <= r && r + |v| <= |s| && s[r..r + |v|] == v)
  decreases |s| - i
{
  if i + |v| > |s| then -1
  else if s[i..i + |v|] == v then i
  else if i == |s| then -1
  else IndexOfFrom(s, v, i + 1)
}

// string.IndexOf(value, StringComparison.Ordinal)
function IndexOfOrdinal(s: CsString, v: CsString): Int32
{
  IndexOfFrom(s, v, 0) as Int32
}

// s[..n] (String.Substring(0, n)): throws ArgumentOutOfRangeException when n < 0 or n > Length.
function SliceTo(s: CsString, n: Int32): Outcome<CsString>
{
  if n < 0 || n as int > |s| then Threw(ArgumentOutOfRangeException)
  else var r: CsString := s[..n]; Returned(r)
}

// ---------------------------------------------------------------------------
// Transliteration: CredentialEndpoints
// ---------------------------------------------------------------------------

const TokenEndpointPath: CsString := "/application/o/token/"

// C# constant-folds $"...ending in {TokenEndpointPath} " + "(an Authentik token endpoint)." (TokenEndpointPath is a const);
// The verifier cannot equate a long literal with a concatenation, so the folded value is written out here.
const MsgTokenShapeText: seq<char> := "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/ (an Authentik token endpoint)."
const MsgTokenShape: CsString := MsgTokenShapeText
const MsgGrpcTlsText: seq<char> := "IVERSON_GRPC_URL must be https:// or a loopback address: the service and acting-user tokens are sent on this channel."
const MsgGrpcTls: CsString := MsgGrpcTlsText
const MsgGrpcPlainText: seq<char> := "IVERSON_GRPC_URL must be a loopback address: this harness's channel is plaintext and carries the service and acting-user tokens."
const MsgGrpcPlain: CsString := MsgGrpcPlainText
const MsgTokenTransportText: seq<char> := "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address: the client secret or assertion is sent to it."
const MsgTokenTransport: CsString := MsgTokenTransportText
const MsgTokenNeedsHttpsText: seq<char> := "IVERSON_TOKEN_ENDPOINT must be https:// when IVERSON_GRPC_URL is not a loopback address: " + "the client library sends client credentials only to an https token endpoint on such a channel."
const MsgTokenNeedsHttps: CsString := MsgTokenNeedsHttpsText
const MsgAuthentikBaseText: seq<char> := "The Authentik base URL (IVERSON_TOKEN_ENDPOINT without its path) must be https:// or a " + "loopback address: user passwords and TOTP codes are sent to it."
const MsgAuthentikBase: CsString := MsgAuthentikBaseText

function Check(grpcUrl: CsString, tokenEndpoint: Option<CsString>, grpcMayUseTls: bool): Outcome<Option<CsString>>
{
  if tokenEndpoint.Some?
     && !(UriIsAbsolute(tokenEndpoint.value)
          && EndsWithOrdinal(tokenEndpoint.value, TokenEndpointPath))
  then Returned(Some(MsgTokenShape))

  else if !UriIsAbsolute(grpcUrl)
          || !(UriIsLoopback(grpcUrl) || (grpcMayUseTls && UriScheme(grpcUrl) == UriSchemeHttps))
  then Returned(Some(if grpcMayUseTls then MsgGrpcTls else MsgGrpcPlain))

  else if tokenEndpoint.Some? && !IsHttpsOrLoopback(tokenEndpoint.value)
  then Returned(Some(MsgTokenTransport))

  else if grpcMayUseTls && !UriIsLoopback(grpcUrl) && tokenEndpoint.Some?
          && !(UriIsAbsolute(tokenEndpoint.value) && UriScheme(tokenEndpoint.value) == UriSchemeHttps)
  then Returned(Some(MsgTokenNeedsHttps))

  else match AuthentikBaseUrl(tokenEndpoint)
    case Threw(e) => Threw(e)
    case Returned(baseUrl) =>
      if !IsHttpsOrLoopback(baseUrl) then Returned(Some(MsgAuthentikBase))
      else Returned(None)
}

function AuthentikBaseUrl(tokenEndpoint: Option<CsString>): Outcome<CsString>
{
  if tokenEndpoint.Some?
  then SliceTo(tokenEndpoint.value, IndexOfOrdinal(tokenEndpoint.value, TokenEndpointPath))
  else var d: CsString := "http://localhost:9000"; Returned(d)
}

function IsLoopback(url: CsString): bool
{
  UriIsAbsolute(url) && UriIsLoopback(url)
}

function IsHttpsOrLoopback(url: CsString): bool
{
  UriIsAbsolute(url) && (UriScheme(url) == UriSchemeHttps || UriIsLoopback(url))
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma IndexOfFromFindsSuffix(s: CsString, v: CsString, i: nat)
  requires EndsWithOrdinal(s, v)
  requires i <= |s| - |v|
  ensures IndexOfFrom(s, v, i) >= i
  decreases |s| - i
{
  if i < |s| - |v| {
    IndexOfFromFindsSuffix(s, v, i + 1);
  }
}

lemma AuthentikBaseUrlReturnsWhenShapeOk(tokenEndpoint: Option<CsString>)
  requires tokenEndpoint.Some? ==> EndsWithOrdinal(tokenEndpoint.value, TokenEndpointPath)
  ensures AuthentikBaseUrl(tokenEndpoint).Returned?
{
  if tokenEndpoint.Some? {
    IndexOfFromFindsSuffix(tokenEndpoint.value, TokenEndpointPath, 0);
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "It first checks the token endpoint's `/application/o/token/` shape, so a malformed value fails with a clear message instead of an opaque `IndexOf` crash." (spec :56)
lemma P1_NeverThrows(grpcUrl: CsString, tokenEndpoint: Option<CsString>, grpcMayUseTls: bool)
  ensures Check(grpcUrl, tokenEndpoint, grpcMayUseTls).Returned?
{
  if tokenEndpoint.Some? && !(UriIsAbsolute(tokenEndpoint.value) && EndsWithOrdinal(tokenEndpoint.value, TokenEndpointPath)) {
  } else {
    AuthentikBaseUrlReturnsWhenShapeOk(tokenEndpoint);
  }
}

// Citation: "The gRPC URL must be loopback or `https://`." (spec :56)
lemma P2_AcceptedGrpcUrlIsLoopbackOrHttps(grpcUrl: CsString, tokenEndpoint: Option<CsString>, grpcMayUseTls: bool)
  requires Check(grpcUrl, tokenEndpoint, grpcMayUseTls) == Returned(None)
  ensures UriIsAbsolute(grpcUrl) && (UriIsLoopback(grpcUrl) || (grpcMayUseTls && UriScheme(grpcUrl) == "https"))
{
}

// Citation: "The token endpoint and the Authentik base URL must be `https://` or loopback." (spec :56)
lemma P3_AcceptedTokenEndpointIsHttpsOrLoopback(grpcUrl: CsString, tokenEndpoint: Option<CsString>, grpcMayUseTls: bool)
  requires Check(grpcUrl, tokenEndpoint, grpcMayUseTls) == Returned(None)
  requires tokenEndpoint.Some?
  ensures UriIsAbsolute(tokenEndpoint.value) && (UriScheme(tokenEndpoint.value) == "https" || UriIsLoopback(tokenEndpoint.value))
{
}

// Citation: "The token endpoint and the Authentik base URL must be `https://` or loopback." (spec :56)
lemma P4_AcceptedAuthentikBaseIsHttpsOrLoopback(grpcUrl: CsString, tokenEndpoint: Option<CsString>, grpcMayUseTls: bool)
  requires Check(grpcUrl, tokenEndpoint, grpcMayUseTls) == Returned(None)
  ensures AuthentikBaseUrl(tokenEndpoint).Returned?
  ensures UriIsAbsolute(AuthentikBaseUrl(tokenEndpoint).value) && (UriScheme(AuthentikBaseUrl(tokenEndpoint).value) == "https" || UriIsLoopback(AuthentikBaseUrl(tokenEndpoint).value))
{
}

// Citation: "It first checks the token endpoint's `/application/o/token/` shape" (spec :56)
lemma P5_MalformedTokenEndpointIsNamedFirst(grpcUrl: CsString, tokenEndpoint: Option<CsString>, grpcMayUseTls: bool)
  requires tokenEndpoint.Some?
  requires !(UriIsAbsolute(tokenEndpoint.value) && |tokenEndpoint.value| >= 21 && tokenEndpoint.value[|tokenEndpoint.value| - 21..] == "/application/o/token/")
  ensures Check(grpcUrl, tokenEndpoint, grpcMayUseTls) == Returned(Some("IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/ (an Authentik token endpoint)."))
{
  assert |TokenEndpointPath| == 21;
}

// Citation: "with a non-loopback https:// gRPC URL the client library is not opted in to plaintext credentials, so it refuses a plaintext token endpoint ... refuse that pairing here" (plan :1943-1945, CIR-1 §3.1 C)
lemma P6_NonLoopbackGrpcNeedsHttpsTokenEndpoint(grpcUrl: CsString, tokenEndpoint: Option<CsString>, grpcMayUseTls: bool)
  requires Check(grpcUrl, tokenEndpoint, grpcMayUseTls) == Returned(None)
  requires grpcMayUseTls && !UriIsLoopback(grpcUrl) && tokenEndpoint.Some?
  ensures UriScheme(tokenEndpoint.value) == "https"
{
}
