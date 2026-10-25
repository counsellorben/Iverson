// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T23 S4
// Source: Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java the scheme test and channel builder (180–186)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/Driver.channelTransport.dfy

// ---- Shared modelling vocabulary ----

datatype Option<T> = None | Some(value: T)

// Java strings: UTF-16 code units under --unicode-char false.
type CsString = s: seq<char> | |s| < 0x8000_0000

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

// ---- Library boundary ----

// grpc-java ManagedChannelBuilder.useTransportSecurity() / usePlaintext(), modelled as the
// transport the builder ends up configured with.
datatype JavaTransport = TransportSecurity | Plaintext

// java.lang.String.equalsIgnoreCase (approved axiom; ASCII case pinned).
function EqualsIgnoreCase(a: CsString, b: CsString): bool
  ensures IsAscii(a) && IsAscii(b) ==> (EqualsIgnoreCase(a, b) <==> AsciiLower(a) == AsciiLower(b))

// The Java literal "https" at Driver.java:181.
const Https: CsString := "https"

// ---- Transliteration of Driver.java:180–186 ----
// `scheme` is java.net.URI.getScheme(), which returns null for a scheme-less URI.
function ChannelTransport(scheme: Option<CsString>): JavaTransport
{
  // Driver.java:181  if ("https".equalsIgnoreCase(grpcUri.getScheme()))
  // String.equalsIgnoreCase(null) returns false (documented java.lang.String contract).
  var isHttps := match scheme
    case None => false
    case Some(s) => EqualsIgnoreCase(Https, s);
  if isHttps then
    // Driver.java:182  channelBuilder.useTransportSecurity();
    TransportSecurity
  else
    // Driver.java:184  channelBuilder.usePlaintext();
    Plaintext
}

// ---- Helpers ----

lemma HttpsFacts()
  ensures IsAscii(Https)
  ensures AsciiLower(Https) == "https"
{
  assert Https == ['h', 't', 't', 'p', 's'];
  assert AsciiLower(Https) == ['h', 't', 't', 'p', 's'];
}

// ---- Properties ----

// Citation: "… `Driver.java:177` … hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446)
lemma P1_TlsExactlyForAnHttpsScheme(scheme: Option<CsString>)
  requires scheme.Some? ==> IsAscii(scheme.value)
  ensures ChannelTransport(scheme) == (if scheme.Some? && AsciiLower(scheme.value) == "https" then TransportSecurity else Plaintext)
{
  HttpsFacts();
  match scheme {
    case None =>
    case Some(s) =>
      assert EqualsIgnoreCase(Https, s) <==> AsciiLower(Https) == AsciiLower(s);
  }
}
