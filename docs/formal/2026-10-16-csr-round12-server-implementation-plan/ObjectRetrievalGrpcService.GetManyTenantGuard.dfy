// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T9 S5
// Source: Iverson.Server/Iverson.Api/Grpc/ObjectRetrievalGrpcService.cs `GetMany`, lines from the new `if (decision.TenantColumn is null)` guard through the `FetchManyByKeysAsync` call (5f)
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/ObjectRetrievalGrpcService.GetManyTenantGuard.dfy

// ---- Shared vocabulary ----------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

// ---- Candidate model ------------------------------------------------------

// AuthorizationDecision (IRowFieldAuthorizationEvaluator.cs:16), the fields the range reads.
datatype Decision = Decision(Denied: bool, TenantColumn: Option<CsString>, TenantValue: Option<CsString>)

// EntityAccess (IRecordStoreRoles.cs:152-181): ForTenant(t) = new(false, t);
// CrossTenantMaintenance = new(true, null). Record-struct equality on (CrossTenant, TenantId),
// so the two constructors are distinct values exactly as these datatype constructors are.
datatype EntityAccess = ForTenant(tenant: Option<CsString>) | CrossTenantMaintenance

// Proceeded: control falls through past FetchManyByKeysAsync; Rpc: a thrown RpcException.
datatype GuardOutcome = Proceeded | Rpc(code: StatusCode, detail: seq<char>)

// ObjectRetrievalGrpcService.cs:115-121. The second component is the sequence of EntityAccess
// values passed to FetchManyByKeysAsync (zero or one call).
function GetManyRead(dec: Decision): (GuardOutcome, seq<EntityAccess>)
{
  // 115: if (decision.TenantColumn is null)
  if dec.TenantColumn.None? then
    // 116: throw new RpcException(new Status(StatusCode.Internal, "The type declares no tenant boundary."));
    (Rpc(Internal, "The type declares no tenant boundary."), [])
  else
    // 118-121: await _entities.FetchManyByKeysAsync(..., keys, EntityAccess.ForTenant(decision.TenantValue));
    (Proceeded, [ForTenant(dec.TenantValue)])
}

// ---- Properties -----------------------------------------------------------

// Citation: "The api's maintenance-role reads and deletes for types without a tenant column, and the outbox writer's owner-role branch for a null tenant, become fail-closed errors:" (spec §6.4)
lemma P1_ATenantlessDecisionIsRefusedBeforeTheRead(dec: Decision)
  requires !dec.Denied && dec.TenantColumn.None?
  ensures GetManyRead(dec) == (Rpc(Internal, "The type declares no tenant boundary."), [])
{
}

// Citation: "**The api never enters BYPASSRLS.**" (spec §6.4)
lemma P2_TheReadNeverRunsAcrossTenants(dec: Decision)
  ensures forall a :: a in GetManyRead(dec).1 ==> a == ForTenant(dec.TenantValue)
{
}
