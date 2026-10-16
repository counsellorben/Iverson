// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan.md, T1 S3, T1 S5
// Source: Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/TransportGuardedCredentials.java and Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/OAuth2ClientCredentials.java TransportGuardedCredentials.applyRequestMetadata and refusedWithoutTls; the security-level guard in OAuth2ClientCredentials.applyRequestMetadata
// Code ref: 50796aedc1e8d3a08792db01c606f0505cae9a86
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --unicode-char false --allow-deprecation docs/formal/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan/TransportGuardedCredentials.refusedWithoutTls.dfy

// ---------------------------------------------------------------------------
// Library-boundary model (grpc-java 1.84.0). Every external call is modelled,
// none is axiomatised.
// ---------------------------------------------------------------------------

// io.grpc.SecurityLevel: the three enum constants.
datatype SecurityLevel = NONE | INTEGRITY | PRIVACY_AND_INTEGRITY

// io.grpc.Status.Code (the members this code can reach, plus OK).
datatype StatusCode = OK | UNAUTHENTICATED

// io.grpc.Status: a code plus an optional description (Status.UNAUTHENTICATED
// has no description; withDescription replaces it).
datatype Option<T> = None | Some(value: T)
datatype Status = Status(code: StatusCode, description: Option<string>)

// Status.UNAUTHENTICATED
const STATUS_UNAUTHENTICATED: Status := Status(UNAUTHENTICATED, None)

// Status.withDescription(String): a copy with the description replaced.
function WithDescription(s: Status, d: string): Status
{
  Status(s.code, Some(d))
}

// One call on the MetadataApplier: applier.fail(Status). (applier.apply is
// reached only inside the delegated path, which the model collapses to
// Delegated.)
datatype ApplierEvent = Fail(status: Status)

// What a call to applyRequestMetadata does, as observed by the caller:
//   Failed(code, description): the applier's single fail(Status) call;
//   Delegated: the wrapped/inner credentials path ran (TransportGuardedCredentials:
//     inner.applyRequestMetadata with the original RequestInfo; OAuth2ClientCredentials:
//     executor.execute(token-attach lambda)) and the guard touched no applier;
//   Anomalous: any other applier history from the guard (unreachable; kept so the
//     mapping from applier events to the outcome is total, not assumed).
datatype Outcome =
  | Failed(code: StatusCode, description: string)
  | Delegated
  | Anomalous(events: seq<ApplierEvent>)

// ---------------------------------------------------------------------------
// TransportGuardedCredentials.refusedWithoutTls (TransportGuardedCredentials.java:40-48)
// requestInfo is reduced to the one thing read from it, getSecurityLevel().
// Returns the boolean result and the applier calls made, in order.
// ---------------------------------------------------------------------------
function RefusedWithoutTls(securityLevel: SecurityLevel, allowInsecureCredentials: bool): (bool, seq<ApplierEvent>)
{
  if allowInsecureCredentials || securityLevel == PRIVACY_AND_INTEGRITY then
    (false, [])
  else
    var applierEvents := [Fail(WithDescription(STATUS_UNAUTHENTICATED,
      "call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false"))];
    (true, applierEvents)
}

// Reads the observable outcome off the guard's applier history.
function OutcomeOfFailure(events: seq<ApplierEvent>): Outcome
{
  if |events| == 1 && events[0].status.description.Some? then
    Failed(events[0].status.code, events[0].status.description.value)
  else
    Anomalous(events)
}

// ---------------------------------------------------------------------------
// TransportGuardedCredentials.applyRequestMetadata (TransportGuardedCredentials.java:27-34)
// ---------------------------------------------------------------------------
function ApplyRequestMetadata(level: SecurityLevel, allowInsecureCredentials: bool): Outcome
{
  var (refused, events) := RefusedWithoutTls(level, allowInsecureCredentials);
  if !refused then
    // inner.applyRequestMetadata(requestInfo, executor, applier)
    if events == [] then Delegated else Anomalous(events)
  else
    OutcomeOfFailure(events)
}

// ---------------------------------------------------------------------------
// OAuth2ClientCredentials.applyRequestMetadata, the guard (OAuth2ClientCredentials.java:78-83)
// ---------------------------------------------------------------------------
function OAuth2ApplyRequestMetadata(level: SecurityLevel, allowInsecureCredentials: bool): Outcome
{
  var (refused, events) := RefusedWithoutTls(level, allowInsecureCredentials);
  if refused then
    // return;
    OutcomeOfFailure(events)
  else
    // executor.execute(() -> { ... token fetch and applier.apply ... })
    if events == [] then Delegated else Anomalous(events)
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "when `requestInfo.getSecurityLevel()` is not `PRIVACY_AND_INTEGRITY` and the flag is false, it calls `applier.fail(Status.UNAUTHENTICATED…)`" (spec :36); "`OAuth2ClientCredentials.applyRequestMetadata` applies the same security-level check" (spec :41)
lemma P1_RefusesWithoutTlsOrOptIn(level: SecurityLevel, allowInsecureCredentials: bool)
  requires !allowInsecureCredentials && level != PRIVACY_AND_INTEGRITY
  ensures ApplyRequestMetadata(level, allowInsecureCredentials) == Failed(UNAUTHENTICATED, "call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false")
  ensures OAuth2ApplyRequestMetadata(level, allowInsecureCredentials) == Failed(UNAUTHENTICATED, "call credentials are sent only over TLS; this channel is not TLS and allowInsecureCredentials is false")
{
}

// Citation: "otherwise it delegates with the original `RequestInfo`" (spec :36); "`OAuth2ClientCredentials.applyRequestMetadata` applies the same security-level check" (spec :41)
lemma P2_DelegatesOverTlsOrWithOptIn(level: SecurityLevel, allowInsecureCredentials: bool)
  requires allowInsecureCredentials || level == PRIVACY_AND_INTEGRITY
  ensures ApplyRequestMetadata(level, allowInsecureCredentials) == Delegated
  ensures OAuth2ApplyRequestMetadata(level, allowInsecureCredentials) == Delegated
{
}
