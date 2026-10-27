// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T13 S1
// Source: Iverson.Server/deploy/scripts/mint_acting_user_token.py and Iverson.Server/Iverson.LoadTest/scripts/teacher_rerank.py each script's is_https_or_loopback; mint main's --base-url guard (its first lines, before any request); teacher main's VLLM_API_KEY lines (its first lines, through the sys.exit refusal)
// Replay base: a6c008dce9e4c846f8c34456ddd6297636d31979
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/CredentialEgress.dfy

// ---------------------------------------------------------------------------
// Python strings: sequences of code points.
newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

// "https"
const Https: Str := [104, 116, 116, 112, 115]
// "localhost"
const Localhost: Str := [108, 111, 99, 97, 108, 104, 111, 115, 116]

// ---------------------------------------------------------------------------
// Approved axioms (library boundary).
// urllib.parse.urlsplit(url) -> (scheme, hostname); hostname None when absent.
function UrlSplit(url: Str): (Str, Option<Str>)

// ipaddress.ip_address(host).is_loopback; None when ip_address raises ValueError.
function IpLoopback(host: Str): Option<bool>

// ---------------------------------------------------------------------------
// mint_acting_user_token.py:101-111  is_https_or_loopback(url)
function MintIsHttpsOrLoopback(url: Str): bool
{
  var parts := UrlSplit(url);
  var scheme := parts.0;
  var hostname := parts.1;
  // if not parts.scheme or not parts.hostname: return False
  if |scheme| == 0 || hostname.None? || |hostname.value| == 0 then false
  // if parts.scheme == "https" or parts.hostname == "localhost": return True
  else if scheme == Https || hostname.value == Localhost then true
  // try: return ipaddress.ip_address(parts.hostname).is_loopback
  // except ValueError: return False
  else
    match IpLoopback(hostname.value)
    case Some(b) => b
    case None => false
}

// teacher_rerank.py:608-619  is_https_or_loopback(url)
function TeacherIsHttpsOrLoopback(url: Str): bool
{
  var parts := UrlSplit(url);
  var scheme := parts.0;
  var hostname := parts.1;
  if |scheme| == 0 || hostname.None? || |hostname.value| == 0 then false
  else if scheme == Https || hostname.value == Localhost then true
  else
    match IpLoopback(hostname.value)
    case Some(b) => b
    case None => false
}

// ---------------------------------------------------------------------------
// mint_acting_user_token.py:494-496  main()'s --base-url guard
datatype MintOutcome = MintRefuse | MintProceed

function MintGuard(baseUrl: Option<Str>): MintOutcome
{
  // if args.base_url is not None and not is_https_or_loopback(args.base_url): ... return 2
  if baseUrl.Some? && !MintIsHttpsOrLoopback(baseUrl.value) then MintRefuse
  else MintProceed
}

// teacher_rerank.py:626-629  main()'s VLLM_API_KEY lines
datatype TeacherOutcome = TeacherExit | TeacherProceed(key: Option<Str>)

function TeacherKey(base: Str, env: Option<Str>): TeacherOutcome
{
  // args.api_key = os.environ.get("VLLM_API_KEY") or None
  var apiKey: Option<Str> := if env.Some? && |env.value| > 0 then env else None;
  // if args.api_key and not is_https_or_loopback(args.base_url): sys.exit(...)
  if apiKey.Some? && |apiKey.value| > 0 && !TeacherIsHttpsOrLoopback(base) then TeacherExit
  else TeacherProceed(apiKey)
}

// ---------------------------------------------------------------------------
// Properties.

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
