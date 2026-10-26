// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md @ f6f5db2cbea0425d938739f73a8d6e9147479682
// Source: §5 items 1-3, lines 181-183
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-design/ToolingCredentialEgress.dfy
// Round: 1

datatype Option<T> = None | Some(value: T)
datatype Url = Url(scheme: string, host: string)

// Approved axiom: the loopback-host test of CredentialEndpoints.IsHttpsOrLoopback.
predicate IsLoopbackHost(host: string)

// Line 182: "must be `https` or a loopback host (the rule of CredentialEndpoints.IsHttpsOrLoopback)"
predicate HttpsOrLoopback(u: Url)
{
  u.scheme == "https" || IsLoopbackHost(u.host)
}

// Line 182: "checked before any request" -- the mint script sends a request iff the check passes.
predicate MintSends(base: Url)
{
  HttpsOrLoopback(base)
}

// Line 183: key "comes only from VLLM_API_KEY" (--api-key dropped, argvKey ignored),
// "sent only to an https or loopback --base-url".
function TeacherKeySent(base: Url, envKey: Option<string>, argvKey: Option<string>): Option<string>
{
  if HttpsOrLoopback(base) then envKey else None
}

datatype TokenResult = TokenResult(requests: nat, ok: bool)

// Line 181: AllowAutoRedirect = false, so any response is one request; a 3xx is a failed request.
// Non-3xx: success iff 2xx (the spec states only the 3xx case; other statuses are not constrained).
function TokenRequest(firstStatus: int): TokenResult
{
  if 300 <= firstStatus < 400 then TokenResult(1, false)
  else TokenResult(1, 200 <= firstStatus < 300)
}

// Citation: "**`mint_acting_user_token.py --base-url`** must be `https` or a loopback host (the rule of `CredentialEndpoints.IsHttpsOrLoopback`), checked before any request." (line 182)
lemma P1_MintRefusesPlainRemote(base: Url)
  requires base.scheme != "https" && !IsLoopbackHost(base.host)
  ensures !MintSends(base)
{
}

// Citation: same (line 182)
lemma P2_MintAllowsHttpsOrLoopback(base: Url)
  requires base.scheme == "https" || IsLoopbackHost(base.host)
  ensures MintSends(base)
{
}

// Citation: "drops `--api-key`; the key comes only from `VLLM_API_KEY` … and is sent only to an `https` or loopback `--base-url`." (line 183)
lemma P3_TeacherKeyOnlyFromEnvToSafeUrl(base: Url, envKey: Option<string>, argvKey: Option<string>, k: string)
  requires TeacherKeySent(base, envKey, argvKey) == Some(k)
  ensures envKey == Some(k) && HttpsOrLoopback(base)
{
}

// Citation: "Tests: a 307 yields one request and a failure." and "a 3xx is a failed token request" (line 181)
lemma P4_RedirectIsOneRequestAndFailure(status: int)
  requires 300 <= status < 400
  ensures TokenRequest(status) == TokenResult(1, false)
{
}
