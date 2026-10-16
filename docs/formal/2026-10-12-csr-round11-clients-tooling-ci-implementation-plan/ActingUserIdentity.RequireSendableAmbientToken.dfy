// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan.md, T2 S3, T2 S7
// Source: Iverson.Clients/DotNet/Iverson.Client.Core/ActingUserIdentity.cs, EntityCoordinator.cs and SchemaCatalogClient.cs (all under Iverson.Clients/DotNet/Iverson.Client.Core/) ActingUserIdentity.RequireSendableAmbientToken; EntityCoordinator<T>'s _identity initializer and WithActingUser's guard; SchemaCatalogClient's _actingUserTokenProvider initializer
// Replay base: 7d4669e5896c4df448b2d45c9a4abbf67926b688
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --unicode-char false --allow-deprecation docs/formal/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan/ActingUserIdentity.RequireSendableAmbientToken.dfy

datatype Option<T> = None | Some(value: T)

// A thrown exception is an outcome (InvalidOperationException in every throwing path here).
datatype Outcome<T> = Returned(value: T) | Threw

// Func<Task<string>>: an opaque, non-null delegate reference (its body is never invoked here).
type TokenProvider(==)

// ActingUserIdentity.cs:8-21 — primary-constructor class with two get-only properties.
// A non-null reference; nullability is the Option around it.
datatype Identity = Identity(tokenProvider: Option<TokenProvider>, refusesPlaintextTokens: bool)
{
  // `TokenProvider: not null` in the property pattern.
  const hasTokenProvider: bool := tokenProvider.Some?
}

// ActingUserIdentity.cs:28-37
// identity is { TokenProvider: not null, RefusesPlaintextTokens: true } ? throw ... : identity
function RequireSendableAmbientToken(identity: Option<Identity>): Outcome<Option<Identity>>
{
  if identity.Some? && identity.value.tokenProvider.Some? && identity.value.refusesPlaintextTokens == true then
    Threw
  else
    Returned(identity)
}

// EntityCoordinator.cs:31
// private readonly ActingUserIdentity? _identity = ActingUserIdentity.RequireSendableAmbientToken(identity);
// Returns the value stored in _identity, or Threw when the initializer throws.
function CoordinatorIdentity(identity: Option<Identity>): Outcome<Option<Identity>>
{
  RequireSendableAmbientToken(identity)
}

// EntityCoordinator.cs:50-70
// WithActingUser(tokenProvider): the guard, then `new(..., _identity) { _boundActingUser = tokenProvider }`,
// whose constructor re-runs the _identity initializer on the same identity.
// Returns the new coordinator's _identity, or Threw.
function WithActingUser(identityField: Option<Identity>): Outcome<Option<Identity>>
{
  // `_identity?.RefusesPlaintextTokens != false`: a null-conditional on a bool yields bool?;
  // null != false is true, so a None identity throws.
  var lifted: Option<bool> := if identityField.Some? then Some(identityField.value.refusesPlaintextTokens) else None;
  if lifted != Some(false) then
    Threw
  else
    match CoordinatorIdentity(identityField)
    case Threw => Threw
    case Returned(newIdentity) => Returned(newIdentity)
}

// SchemaCatalogClient.cs:22-23
// private readonly Func<Task<string>>? _actingUserTokenProvider =
//     ActingUserIdentity.RequireSendableAmbientToken(identity)?.TokenProvider;
function SchemaCatalogClientInit(identity: Option<Identity>): Outcome<Option<TokenProvider>>
{
  match RequireSendableAmbientToken(identity)
  case Threw => Threw
  case Returned(checked) =>
    Returned(if checked.Some? then checked.value.tokenProvider else None)
}

// Citation: "`EntityCoordinator<T>`'s constructor throws when the identity carries a token provider and `RefusesPlaintextTokens` is true, mirroring `SchemaCatalogClient`" (spec :46)
lemma P1_CoordinatorRefusesAnUnsendableAmbientToken(identity: Option<Identity>)
  ensures CoordinatorIdentity(identity).Threw? <==> (identity.Some? && identity.value.hasTokenProvider && identity.value.refusesPlaintextTokens)
{
}

// Citation: "`EntityCoordinator<T>.WithActingUser` refuses unless `identity?.RefusesPlaintextTokens == false`." (spec :45)
lemma P2_WithActingUserNeedsAKnownTransport(identityField: Option<Identity>)
  ensures WithActingUser(identityField).Threw? <==> !(identityField.Some? && !identityField.value.refusesPlaintextTokens)
{
}

// Citation: "throws at construction when the identity carries a token provider and refuses plaintext" (spec :47)
lemma P3_SchemaCatalogClientRefusesLikeTheCoordinator(identity: Option<Identity>)
  ensures SchemaCatalogClientInit(identity).Threw? <==> (identity.Some? && identity.value.hasTokenProvider && identity.value.refusesPlaintextTokens)
{
}
