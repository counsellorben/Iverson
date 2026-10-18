// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T9 S5
// Source: Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs `Delete`, lines from the new `if (decision.TenantColumn is null)` guard through the `ExecuteInTransactionAsync` block (5f)
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/ObjectMappingGrpcService.DeleteTenantGuard.dfy

// ── Shared vocabulary ──────────────────────────────────────────────────────

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

// ── Candidate model ────────────────────────────────────────────────────────

// AuthorizationDecision, restricted to the fields the range reads (Denied is
// read by the frozen P1 header; the range itself is reached only after the
// denied / owner / tenant mismatch checks pass).
datatype Decision = Decision(Denied: bool, TenantColumn: Option<CsString>, TenantValue: Option<CsString>)

// Iverson EntityAccess: ForTenant(string?) | CrossTenantMaintenance.
datatype EntityAccess = ForTenant(tenant: Option<CsString>) | CrossTenantMaintenance

// Proceeded: control falls through past the range (to PublishAsync).
// Rpc: a thrown RpcException(new Status(code, detail)).
datatype GuardOutcome = Proceeded | Rpc(code: StatusCode, detail: seq<char>)

// Effects recorded by the modelled library calls, in order:
//   ExecuteInTransactionAsync          -> TxBegun
//   _entities.DeleteAsync(..., access) -> EntityDelete(access)
//   EnqueueDeleteOutboxRowAsync        -> OutboxDeleteRow
datatype TxEffect = TxBegun | EntityDelete(access: EntityAccess) | OutboxDeleteRow

// ObjectMappingGrpcService.cs:348-365
function DeleteWrite(dec: Decision): (GuardOutcome, seq<TxEffect>)
{
  // :348-349  if (decision.TenantColumn is null) throw new RpcException(...Internal...)
  if dec.TenantColumn.None? then
    (Rpc(Internal, "The type declares no tenant boundary."), [])
  else
    // :351      await _txRunner.ExecuteInTransactionAsync(async tx => {
    // :353-357    await _entities.DeleteAsync(tx, ..., EntityAccess.ForTenant(decision.TenantValue));
    // :359-364    await _outboxWriter.EnqueueDeleteOutboxRowAsync(tx, ...);
    // :365      });
    (Proceeded, [TxBegun, EntityDelete(ForTenant(dec.TenantValue)), OutboxDeleteRow])
}

// ── Properties ─────────────────────────────────────────────────────────────

// Citation: "The api's maintenance-role reads and deletes for types without a tenant column, and the outbox writer's owner-role branch for a null tenant, become fail-closed errors:" (spec §6.4)
lemma P1_ATenantlessDecisionIsRefusedBeforeTheTransaction(dec: Decision)
  requires !dec.Denied && dec.TenantColumn.None?
  ensures DeleteWrite(dec) == (Rpc(Internal, "The type declares no tenant boundary."), [])
{
}

// Citation: "**The api never enters BYPASSRLS.**" (spec §6.4)
lemma P2_TheDeleteNeverRunsAcrossTenants(dec: Decision)
  ensures forall e :: e in DeleteWrite(dec).1 && e.EntityDelete? ==> e.access == ForTenant(dec.TenantValue)
{
}
