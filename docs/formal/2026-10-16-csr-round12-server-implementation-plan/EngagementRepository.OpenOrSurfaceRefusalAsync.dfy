// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T10 S3
// Source: Iverson.Server/Iverson.StarRocks/EngagementRepository.cs `OpenOrSurfaceRefusalAsync`, `OpenTenantConnectionAsync`, `OpenProvisionerConnectionAsync`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/EngagementRepository.OpenOrSurfaceRefusalAsync.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary (only what this candidate uses).
// ---------------------------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

// MySqlConnector's MySqlErrorCode.AccessDenied (ER_ACCESS_DENIED_ERROR).
const AccessDenied: int := 1045

// The exception a step can end in.
//   MySqlEx(code, message): a MySqlException with ErrorCode == code;
//   OtherOpenEx:            any other exception (OperationCanceledException, NRE from a null Task, ...);
//   NotReady(notReadyMessage, inner): EngagementNotReadyException(message, inner); the field is not
//   named `message` because Dafny forbids one destructor name with two types across constructors.
datatype OpenExn = MySqlEx(code: int, message: CsString) | OtherOpenEx | NotReady(notReadyMessage: seq<char>, inner: OpenExn)

// The outcome of awaiting `open()` (whether it throws synchronously or returns a faulted Task, the
// try block observes the same exception), and the outcome of each wrapper.
datatype OpenStep = Opened | OpenThrew(e: OpenExn)

// ---------------------------------------------------------------------------------------------
// Transliteration.
// ---------------------------------------------------------------------------------------------

// private static async Task OpenOrSurfaceRefusalAsync(Func<Task> open, string refusal)
function OpenOrSurfaceRefusal(open: OpenStep, refusal: CsString): OpenStep
{
  // try { await open().ConfigureAwait(false); }
  if open.Opened? then Opened
  // catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)
  else if open.e.MySqlEx? && open.e.code == AccessDenied then
    // throw new EngagementNotReadyException(refusal, ex);
    OpenThrew(NotReady(refusal, open.e))
  // any other exception is not caught: it propagates unchanged
  else open
}

// internal static Task OpenTenantConnectionAsync(Func<Task> open) =>
//     OpenOrSurfaceRefusalAsync(open, "StarRocks refused the tenant's login.");
function OpenTenantConnection(open: OpenStep): OpenStep
{
  OpenOrSurfaceRefusal(open, "StarRocks refused the tenant's login.")
}

// internal static Task OpenProvisionerConnectionAsync(Func<Task> open) =>
//     OpenOrSurfaceRefusalAsync(open, "StarRocks refused the provisioner login; it may not have been created yet.");
function OpenProvisionerConnection(open: OpenStep): OpenStep
{
  var refusal: seq<char> := "StarRocks refused the provisioner login; it may not have been created yet.";
  ProvisionerRefusalIsACsString();
  OpenOrSurfaceRefusal(open, refusal)
}

// Proof helper: the 74-code-unit literal is a C# string (Z3 does not unfold its length unaided).
lemma ProvisionerRefusalIsACsString()
  ensures |"StarRocks refused the provisioner login; it may not have been created yet."| < 0x8000_0000
{
  assert "StarRocks refused the provisioner login; it may not have been created yet." ==
    "StarRocks refused the provisioner login; " + "it may not have been created yet.";
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen).
// ---------------------------------------------------------------------------------------------

// Citation: "A recorded tenant whose login fails is a real error: it raises `EngagementNotReadyException`, which the gRPC services map to `Unavailable` and which halts the worker's consumer rather than dead-lettering (as round 11 does for a refused provisioner login)." (spec §7.3)
lemma P1_ARefusedTenantLoginSurfacesAsNotReady(m: CsString)
  ensures OpenTenantConnection(OpenThrew(MySqlEx(AccessDenied, m))) ==
    OpenThrew(NotReady("StarRocks refused the tenant's login.", MySqlEx(AccessDenied, m)))
{
}

// Citation: "(await act.Should().ThrowAsync<MySqlException>()).Which.Should().BeSameAs(original);" (plan T10 S1c, `OtherFailure_OnOpeningATenantConnection_IsRethrownUnchanged`, generalised)
lemma P2_EveryOtherOpenOutcomeIsUnchanged(open: OpenStep)
  requires !(open.OpenThrew? && open.e.MySqlEx? && open.e.code == AccessDenied)
  ensures OpenTenantConnection(open) == open
{
}

// Citation: "(as round 11 does for a refused provisioner login)" (spec §7.3)
lemma P3_ARefusedProvisionerLoginStillSurfacesAsNotReady(m: CsString)
  ensures OpenProvisionerConnection(OpenThrew(MySqlEx(AccessDenied, m))) ==
    OpenThrew(NotReady("StarRocks refused the provisioner login; it may not have been created yet.", MySqlEx(AccessDenied, m)))
{
}
