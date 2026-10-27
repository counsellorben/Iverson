// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T13 S1
// Source: Iverson.Server/deploy/scripts/mint_acting_user_token.py and Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py each script's is_https_or_loopback; mint main's --base-url guard (its first lines, before any request); teacher main's VLLM_API_KEY lines (its first lines, through the sys.exit refusal)
// Code ref: d0eab48b734ab0c7023d50d11a58dd51167028fa
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/CredentialEgress.dfy

// ---------------------------------------------------------------------------
// Strings and helpers
// ---------------------------------------------------------------------------

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

// "https"
const Https: Str := [0x68, 0x74, 0x74, 0x70, 0x73]
// "localhost"
const Localhost: Str := [0x6C, 0x6F, 0x63, 0x61, 0x6C, 0x68, 0x6F, 0x73, 0x74]

// ---------------------------------------------------------------------------
// Approved axioms (library boundary)
// ---------------------------------------------------------------------------

// urllib.parse.urlsplit(url): (scheme, hostname); hostname is None when absent.
function UrlSplit(url: Str): (Str, Option<Str>)

// ipaddress.ip_address(host).is_loopback; None when ip_address raises ValueError.
function IpLoopback(host: Str): Option<bool>

// ---------------------------------------------------------------------------
// mint_acting_user_token.py
// ---------------------------------------------------------------------------

// mint_acting_user_token.py:101-112 is_https_or_loopback
function MintIsHttpsOrLoopback(url: Str): bool
{
  var parts := UrlSplit(url);
  // if not parts.scheme or not parts.hostname: return False
  if |parts.0| == 0 || parts.1.None? || |parts.1.value| == 0 then false
  // if parts.scheme == "https" or parts.hostname == "localhost": return True
  else if parts.0 == Https || parts.1.value == Localhost then true
  // try: return ipaddress.ip_address(parts.hostname).is_loopback
  // except ValueError: return False
  else match IpLoopback(parts.1.value)
    case Some(b) => b
    case None => false
}

datatype MintOutcome = MintRefuse | MintProceed

// mint_acting_user_token.py:494-497, main's --base-url guard
function MintGuard(baseUrl: Option<Str>): MintOutcome
{
  // if args.base_url is not None and not is_https_or_loopback(args.base_url): ... return 2
  if baseUrl.Some? && !MintIsHttpsOrLoopback(baseUrl.value) then MintRefuse
  else MintProceed
}

// ---------------------------------------------------------------------------
// teacher_rerank.py
// ---------------------------------------------------------------------------

// teacher_rerank.py:619-630 is_https_or_loopback
function TeacherIsHttpsOrLoopback(url: Str): bool
{
  var parts := UrlSplit(url);
  // if not parts.scheme or not parts.hostname: return False
  if |parts.0| == 0 || parts.1.None? || |parts.1.value| == 0 then false
  // if parts.scheme == "https" or parts.hostname == "localhost": return True
  else if parts.0 == Https || parts.1.value == Localhost then true
  // try: return ipaddress.ip_address(parts.hostname).is_loopback
  // except ValueError: return False
  else match IpLoopback(parts.1.value)
    case Some(b) => b
    case None => false
}

datatype TeacherOutcome = TeacherExit | TeacherProceed(key: Option<Str>)

// teacher_rerank.py:637-640, main's VLLM_API_KEY lines
// base: args.base_url (--base-url is required=True, so a str); env: os.environ.get("VLLM_API_KEY")
function TeacherKey(base: Str, env: Option<Str>): TeacherOutcome
{
  // args.api_key = os.environ.get("VLLM_API_KEY") or None
  var apiKey: Option<Str> := if env.Some? && |env.value| > 0 then env else None;
  // if args.api_key and not is_https_or_loopback(args.base_url): sys.exit(...)
  if apiKey.Some? && |apiKey.value| > 0 && !TeacherIsHttpsOrLoopback(base) then TeacherExit
  else TeacherProceed(apiKey)
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: design ToolingCredentialEgress.P1_MintRefusesPlainRemote: "**`mint_acting_user_token.py --base-url`** must be `https` or a loopback host … checked before any request." (spec line 182); test `test_a_plain_remote_base_url_is_refused_before_any_request`.
lemma P1_MintRefusesPlainRemote(base: Str)
  requires UrlSplit(base).0 != Https
  requires UrlSplit(base).1.None? || (UrlSplit(base).1.value != Localhost && IpLoopback(UrlSplit(base).1.value) != Some(true))
  ensures MintGuard(Some(base)) == MintRefuse
{
}

// Citation: design ToolingCredentialEgress.P2_MintAllowsHttpsOrLoopback (spec line 182); test `test_an_https_or_loopback_base_url_is_used`.
lemma P2_MintAllowsHttpsOrLoopback(base: Str)
  requires |UrlSplit(base).0| > 0 && UrlSplit(base).1.Some? && |UrlSplit(base).1.value| > 0
  requires UrlSplit(base).0 == Https || UrlSplit(base).1.value == Localhost || IpLoopback(UrlSplit(base).1.value) == Some(true)
  ensures MintGuard(Some(base)) == MintProceed
{
}

// Citation: "Both scripts gain `is_https_or_loopback` (the rule of `CredentialEndpoints.IsHttpsOrLoopback`: https, `localhost`, or a loopback address)" (plan T13 S1).
lemma P3_BothCopiesAgree(url: Str)
  ensures MintIsHttpsOrLoopback(url) == TeacherIsHttpsOrLoopback(url)
{
}

// Citation: design ToolingCredentialEgress.P3_TeacherKeyOnlyFromEnvToSafeUrl: "the key comes only from `VLLM_API_KEY` … and is sent only to an `https` or loopback `--base-url`" (spec line 183); test `test_main_passes_the_environment_key_to_an_https_or_loopback_base_url`.
lemma P4_TeacherKeyOnlyFromEnvToSafeUrl(base: Str, env: Option<Str>, k: Str)
  requires TeacherKey(base, env) == TeacherProceed(Some(k))
  ensures env == Some(k) && |k| > 0 && TeacherIsHttpsOrLoopback(base)
{
}

// Citation: test `test_main_refuses_to_send_the_key_to_a_plain_remote_base_url` (`scripted.calls == 0`).
lemma P5_TeacherRefusesKeyToPlainRemote(base: Str, env: Option<Str>)
  requires env.Some? && |env.value| > 0 && !TeacherIsHttpsOrLoopback(base)
  ensures TeacherKey(base, env) == TeacherExit
{
}
