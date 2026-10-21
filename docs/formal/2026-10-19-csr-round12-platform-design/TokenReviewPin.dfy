// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a782ac7674fde9185ad55bb1a986df3008adf050
// Source: §2.6 "TokenReview on the orchestrator pin", lines 171-178
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/TokenReviewPin.dfy
// Round: 2

datatype Option<T> = None | Some(value: T)

// Outcome of the TokenReview call made by the orchestrator pin (§2.6 line 175):
// either the call raised an exception, or it returned an HTTP response whose body
// carries `status.authenticated` and `status.user.username` (each possibly absent).
datatype ReviewResponse =
  | Exception
  | Http(status: int, authenticated: Option<bool>, username: Option<string>)

// "`system:serviceaccount:<ns>:<rel>-api`" (§2.6 line 175)
function ExpectedUsername(ns: string, rel: string): string
{
  "system:serviceaccount:" + ns + ":" + rel + "-api"
}

// The TokenReview half of the orchestrator pin (§2.6 line 175), cases in the
// spec's order: any exception; any status outside 2xx; `status.authenticated`
// not true; `status.user.username` differs. Every refusal is `invalid_grant`;
// the model returns false for a refusal, true for admission.
// The pre-existing claim checks ("keeps its claim checks") are out of this rule's
// scope and not modelled.
predicate PinAdmits(resp: ReviewResponse, expected: string)
{
  if resp.Exception? then false
  else if !(200 <= resp.status < 300) then false
  else if resp.authenticated != Some(true) then false
  else if resp.username != Some(expected) then false
  else true
}

// Citation: "It refuses (`invalid_grant`) on any exception, on any response status outside 2xx (a successful review answers 201)" (§2.6 line 175)
lemma P1_SuccessAdmitted(expected: string)
  ensures PinAdmits(Http(201, Some(true), Some(expected)), expected)
{
}

// Citation: same sentence (§2.6 line 175)
lemma P2_ErrorsRefused(resp: ReviewResponse, expected: string)
  requires resp.Exception? || (resp.Http? && !(200 <= resp.status < 300))
  ensures !PinAdmits(resp, expected)
{
}

// Citation: "It requires `status.authenticated` true and `status.user.username` = `system:serviceaccount:<ns>:<rel>-api`." (§2.6 line 175)
lemma P3_WrongIdentityRefused(resp: ReviewResponse, expected: string)
  requires resp.Http? && (resp.authenticated != Some(true) || resp.username != Some(expected))
  ensures !PinAdmits(resp, expected)
{
}
