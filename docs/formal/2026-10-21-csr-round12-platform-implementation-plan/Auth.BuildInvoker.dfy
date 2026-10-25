// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T23 S4
// Source: Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Auth.cs BuildInvoker (24–74)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/Auth.BuildInvoker.dfy

// ---- Shared modelling vocabulary ----
datatype Option<T> = None | Some(value: T)

type CsString = s: seq<char> | |s| < 0x8000_0000

predicate IsAscii(s: seq<char>)
{
  forall i :: 0 <= i < |s| ==> (s[i] as int) < 128
}

function AsciiLowerChar(c: char): char
{
  if 'A' <= c <= 'Z' then ((c as int) + 32) as char else c
}

function AsciiLower(s: seq<char>): seq<char>
{
  seq(|s|, i requires 0 <= i < |s| => AsciiLowerChar(s[i]))
}

// ---- Library boundary (approved axiom) ----
function StartsWithOrdinalIgnoreCase(s: CsString, prefix: CsString): bool
  ensures IsAscii(s) && IsAscii(prefix) ==> (StartsWithOrdinalIgnoreCase(s, prefix) <==> |s| >= |prefix| && AsciiLower(s[..|prefix|]) == AsciiLower(prefix))

// ---- Modelled library calls ----
// string.IsNullOrEmpty(string?)
predicate IsNullOrEmpty(s: Option<CsString>)
{
  s.None? || |s.value| == 0
}

// ChannelCredentials.SecureSsl / ChannelCredentials.Insecure
datatype Transport = SecureSsl | Insecure

// The "https://" literal (Auth.cs:35).
const HttpsPrefix: CsString := "https://"

// grpcUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase), as in HarnessChannel.Create.
predicate HasHttpsScheme(grpcUrl: CsString)
{
  StartsWithOrdinalIgnoreCase(grpcUrl, HttpsPrefix)
}

// What BuildInvoker configures on the channel: the transport the call credentials are combined
// with (None: options.Credentials never set, no call credentials attached), and
// GrpcChannelOptions.UnsafeUseInsecureChannelCallCredentials.
datatype InvokerPlan = InvokerPlan(credentials: Option<Transport>, unsafeInsecureCallCredentials: bool)

// ---- Transliteration of Auth.BuildInvoker (Auth.cs:24–74) ----
// actingToken (Auth.cs:29) is dropped: it only feeds ActingUserInterceptor, which is ignored.
function BuildInvoker(grpcUrl: CsString, clientId: Option<CsString>, clientSecret: Option<CsString>, tokenEndpoint: Option<CsString>, serviceToken: Option<CsString>): InvokerPlan
{
  var tls := StartsWithOrdinalIgnoreCase(grpcUrl, HttpsPrefix);                  // :35
  var transport := if tls then SecureSsl else Insecure;                         // :36
  var unsafeInsecure := !tls;                                                   // :37–42
  if !IsNullOrEmpty(serviceToken) then                                          // :50
    InvokerPlan(Some(transport), unsafeInsecure)                                // :52–57
  else if !IsNullOrEmpty(clientId) &&                                           // :59
          !IsNullOrEmpty(clientSecret) &&                                       // :60
          !IsNullOrEmpty(tokenEndpoint) then                                    // :61
    InvokerPlan(Some(transport), unsafeInsecure)                                // :63–70
  else
    InvokerPlan(None, unsafeInsecure)                                           // :73–74
}

// ---- Properties ----
// Citation: "… the five drivers (`Auth.cs:52, 65`; …) hard-code plaintext channel credentials, so each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446)
lemma P1_TheSchemePicksTheTransport(grpcUrl: CsString, clientId: Option<CsString>, clientSecret: Option<CsString>, tokenEndpoint: Option<CsString>, serviceToken: Option<CsString>)
  requires IsAscii(grpcUrl)
  requires BuildInvoker(grpcUrl, clientId, clientSecret, tokenEndpoint, serviceToken).credentials.Some?
  ensures BuildInvoker(grpcUrl, clientId, clientSecret, tokenEndpoint, serviceToken).credentials.value == (if HasHttpsScheme(grpcUrl) then SecureSsl else Insecure)
       && BuildInvoker(grpcUrl, clientId, clientSecret, tokenEndpoint, serviceToken).unsafeInsecureCallCredentials == !HasHttpsScheme(grpcUrl)
{
}
