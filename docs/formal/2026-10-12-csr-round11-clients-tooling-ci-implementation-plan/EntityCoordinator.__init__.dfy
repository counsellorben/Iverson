// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan.md, T2 S11
// Source: Iverson.Clients/Python/iverson_client/core.py EntityCoordinator.__init__'s acting_user_token guard and with_acting_user's guard
// Code ref: 50796aedc1e8d3a08792db01c606f0505cae9a86
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions docs/formal/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan/EntityCoordinator.__init__.dfy

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// Python str is a sequence of code points.
newtype CodePoint = x: int | 0 <= x < 0x110000

// Python `str | None`.
datatype Option<T> = None | Some(value: T)

// Outcome of a guard: a raised ValueError, or fall-through to the rest of the body.
datatype GuardOutcome = Threw | Continue

// Outcome of a whole method: a raised ValueError, or a returned value.
datatype Outcome<T> = Raised | Returned(value: T)

// The observable state of an EntityCoordinator that the guards read and write.
// The gRPC stubs, entity class and metadata fields are not read by either guard and are elided.
datatype Coordinator = Coordinator(actingUserToken: Option<seq<CodePoint>>, refusePlaintextToken: bool)

// core.py:644-652: `if acting_user_token is not None and refuse_plaintext_token: raise ValueError(...)`
function InitGuard(token: Option<seq<CodePoint>>, refusePlaintextToken: bool): GuardOutcome
{
  if token.Some? && refusePlaintextToken then
    Threw
  else
    Continue
}

// core.py:634-666: the whole constructor. `hasIversonMeta` models
// `getattr(entity_class, "_iverson_meta", None) is not None` (core.py:653-657),
// an external reflection call modelled as an input.
function Init(token: Option<seq<CodePoint>>, refusePlaintextToken: bool, hasIversonMeta: bool): Outcome<Coordinator>
{
  match InitGuard(token, refusePlaintextToken)
  case Threw => Raised
  case Continue =>
    if !hasIversonMeta then
      Raised
    else
      Returned(Coordinator(token, refusePlaintextToken))
}

// core.py:676-684: `if self._refuse_plaintext_token: raise ValueError(...)`
function WithActingUserGuard(refusePlaintextToken: bool): GuardOutcome
{
  if refusePlaintextToken then
    Threw
  else
    Continue
}

// core.py:668-687: the whole method. `copy.copy(self)` then rebinding `_acting_user_token`
// is a record update on the copy; `self` is untouched.
function WithActingUser(self: Coordinator, token: seq<CodePoint>): Outcome<Coordinator>
{
  match WithActingUserGuard(self.refusePlaintextToken)
  case Threw => Raised
  case Continue => Returned(self.(actingUserToken := Some(token)))
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "`EntityCoordinator.__init__` raises when it is given an ambient `acting_user_token` while `refuse_plaintext_token` is true" (spec :50)
lemma P1_InitRefusesAnAmbientTokenWhileRefusing(token: Option<seq<CodePoint>>, refusePlaintextToken: bool)
  ensures InitGuard(token, refusePlaintextToken).Threw? <==> (token.Some? && refusePlaintextToken)
{
}

// Citation: "The hand-built `EntityCoordinator`'s `refuse_plaintext_token` defaults to `True`" (spec :50); "Python `with_acting_user` refuses first" (spec :217, VA 39)
lemma P2_WithActingUserRefusesWhileRefusing(refusePlaintextToken: bool)
  ensures WithActingUserGuard(refusePlaintextToken).Threw? <==> refusePlaintextToken
{
}
