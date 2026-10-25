// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T23 S4
// Source: Iverson.Clients/TypeScript/conformance/driver.ts parseGrpcAddress (240–252)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/driver.parseGrpcAddress.dfy

// ---- Shared vocabulary ----
datatype Option<T> = None | Some(value: T)
type CsString = s: seq<char> | |s| < 0x8000_0000

// ---- Candidate model ----
datatype UrlInfo = UrlInfo(protocol: CsString, hostname: CsString, port: CsString)

// Named string constants (Library boundary bullet).
const HttpsProtocol: CsString := "https:"
const HttpSchemePrefix: CsString := "http://"
const SchemeSeparator: CsString := "://"

// ---- Approved axioms (library boundary) ----
// WHATWG `new URL(s)`: throws -> None.
function UrlParse(s: CsString): Option<UrlInfo>
  ensures |s| >= 7 && s[..7] == "http://" && UrlParse(s).Some? ==> UrlParse(s).value.protocol == "http:"

type Number

function NumberFromString(s: CsString): Number

predicate NumberIsPositiveInteger(n: Number)

// ---- Modelled library calls ----
// String.prototype.includes(sub): ordinal substring search over UTF-16 code units.
predicate Contains(s: seq<char>, sub: seq<char>)
  decreases |s|
{
  if |sub| <= |s| && s[..|sub|] == sub then true
  else if |s| == 0 then false
  else Contains(s[1..], sub)
}

// ---- Transliteration ----
// The object { host, port, useTls }, or a thrown Error.
datatype ParsedAddr = ParsedAddr(host: CsString, port: Number, useTls: bool) | Throws

function ParseGrpcAddress(addr: CsString): ParsedAddr
{
  // 241: const withScheme = addr.includes('://') ? addr : `http://${addr}`;
  //      (template concatenation past the maximum string length throws RangeError)
  if !Contains(addr, SchemeSeparator) && |HttpSchemePrefix + addr| >= 0x8000_0000 then Throws
  else
  var withScheme: CsString := if Contains(addr, SchemeSeparator) then addr else HttpSchemePrefix + addr;
  // 242-247: try { url = new URL(withScheme) } catch { throw ... }
  var parsed := UrlParse(withScheme);
  if parsed.None? then Throws
  else
  var url := parsed.value;
  // 248: if (!url.hostname) throw ...
  if |url.hostname| == 0 then Throws
  else
  // 249: const port = url.port ? Number(url.port) : url.protocol === 'https:' ? 443 : 80;
  //      (the literals 443 / 80 modelled as Number("443") / Number("80"))
  var port := if |url.port| != 0 then NumberFromString(url.port)
              else if url.protocol == HttpsProtocol then NumberFromString("443")
              else NumberFromString("80");
  // 250: if (!Number.isInteger(port) || port <= 0) throw ...
  if !NumberIsPositiveInteger(port) then Throws
  else
  // 251: return { host: url.hostname, port, useTls: url.protocol === 'https:' };
  ParsedAddr(url.hostname, port, url.protocol == HttpsProtocol)
}

// ---- Properties ----
// Citation: "each moves to TLS credentials chosen by the URL scheme" (§4.6 line 446)
lemma P1_TlsExactlyForAnHttpsUrl(addr: CsString)
  requires ParseGrpcAddress(addr).ParsedAddr?
  ensures ParseGrpcAddress(addr).useTls <==> (Contains(addr, "://") && UrlParse(addr).Some? && UrlParse(addr).value.protocol == HttpsProtocol)
{
  if !Contains(addr, SchemeSeparator) {
    var w: CsString := HttpSchemePrefix + addr;
    assert w[..7] == "http://";
  }
}
