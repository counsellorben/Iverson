// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T23 S4
// Source: Iverson.Clients/Go/conformance/main.go transportOption, grpcUsesTLS (248–259)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/conformance.grpcUsesTLS.dfy

// ── Shared vocabulary ────────────────────────────────────────────────────────
// Go strings are byte sequences.
newtype Byte = x: int | 0 <= x < 256

predicate IsAscii(s: seq<Byte>)
{
  forall i :: 0 <= i < |s| ==> s[i] < 128
}

function AsciiLowerByte(b: Byte): Byte
{
  if 65 <= b <= 90 then b + 32 else b
}

// s with A–Z mapped to a–z, every other unit kept.
function AsciiLower(s: seq<Byte>): seq<Byte>
{
  seq(|s|, i requires 0 <= i < |s| => AsciiLowerByte(s[i]))
}

// "https://"
const HttpsBytes: seq<Byte> := [104, 116, 116, 112, 115, 58, 47, 47]

// ── Library boundary ─────────────────────────────────────────────────────────
// Approved axiom: strings.ToLower (its ASCII case pinned).
function StringsToLower(s: seq<Byte>): seq<Byte>
  ensures IsAscii(s) ==> StringsToLower(s) == AsciiLower(s)

// strings.HasPrefix (modelled): len(s) >= len(prefix) && s[0:len(prefix)] == prefix
function StringsHasPrefix(s: seq<Byte>, prefix: seq<Byte>): bool
{
  |s| >= |prefix| && s[..|prefix|] == prefix
}

// grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{})) / grpc.WithInsecure()
datatype DialTransport = TlsCreds | InsecureCreds

// ── Transliteration (main.go 248–259) ────────────────────────────────────────
function transportOption(addr: seq<Byte>): DialTransport
{
  if grpcUsesTLS(addr) then
    TlsCreds
  else
    InsecureCreds
}

function grpcUsesTLS(addr: seq<Byte>): bool
{
  StringsHasPrefix(StringsToLower(addr), HttpsBytes)
}

// ── Helpers ──────────────────────────────────────────────────────────────────
lemma AsciiLowerPrefix(s: seq<Byte>, n: nat)
  requires n <= |s|
  ensures AsciiLower(s)[..n] == AsciiLower(s[..n])
{
}

// ── Properties ───────────────────────────────────────────────────────────────
// Citation: test `TestGrpcUsesTLSFollowsTheScheme` (T23 S1 1d: `https://`, `HTTPS://` true; `http://`, bare `host:port` false), generalised; "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446)
lemma P1_TlsExactlyForAnHttpsScheme(addr: seq<Byte>)
  requires IsAscii(addr)
  ensures grpcUsesTLS(addr) <==> (|addr| >= 8 && AsciiLower(addr[..8]) == HttpsBytes)
{
  assert StringsToLower(addr) == AsciiLower(addr);
  assert |AsciiLower(addr)| == |addr|;
  if |addr| >= 8 {
    AsciiLowerPrefix(addr, 8);
  }
}

// Citation: as P1
lemma P2_DialOptionFollowsTheScheme(addr: seq<Byte>)
  ensures transportOption(addr) == (if grpcUsesTLS(addr) then TlsCreds else InsecureCreds)
{
}
