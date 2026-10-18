// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T8 S3
// Source: Iverson.Server/Iverson.Api/Program.cs the `/admin/reconcile/{typeName}` handler from `privileged.SubmitAsync` to its return (3e), and the `/admin/dlq/{id}/replay` handler from `dlq.GetUnreplayedSummaryAsync` to its return (3f)
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/Program.PrivilegedAdminRoutes.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

newtype Int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

function SerializeReconcilePayload(typeName: CsString): CsString

function SerializeReplayPayload(id: Guid): CsString

type Guid(==)

// ---------------------------------------------------------------------------
// Model types
// ---------------------------------------------------------------------------

// PrivilegedRequestResult(StatusCode Code, string? Message, int? Count)
datatype PResult = PResult(Code: StatusCode, Message: Option<seq<char>>, Count: Option<Int32>)

// PrivilegedRequestWaits: the two waits these routes read, as TimeSpan ticks.
datatype Waits = Waits(Reconcile: int, Replay: int)

datatype Body = ReconciledBody(reconciledCount: Option<Int32>, typeName: CsString) | ReplayedBody(id: Guid, topic: CsString)

datatype Http = Ok200(body: Body) | Accepted202(requestId: Guid) | NotFound404(error: Option<seq<char>>) | Forbidden403 | Error500(error: Option<seq<char>>)

// IDlqRepository.GetUnreplayedSummaryAsync's row: the columns the api's checks read.
datatype Summary = Summary(SourceTopic: CsString, TenantId: Option<CsString>)

// The privileged-request client calls a route makes, in order.
datatype RouteCall = Submitted(kind: seq<char>, expiresAfter: Option<int>) | Waited(timeout: int)

const ReconcileTypeKind: seq<char> := "ReconcileType"   // PrivilegedRequestKinds.ReconcileType
const ReplayDlqKind: seq<char> := "ReplayDlq"           // PrivilegedRequestKinds.ReplayDlq

// Guid.ToString(): a library call with no approved contract, so its value is left unspecified
// (an arbitrary string per Guid, chosen by Hilbert choice; no trusted declaration).
ghost function GuidToString(id: Guid): CsString
{
  var s: CsString :| s == [] || true; s
}

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// Program.cs:943-975 (RunPrivilegedAdminRequestAsync, the shared helper both routes now call).
// `requestId` is SubmitAsync's id; `waited` is PrivilegedRequestResult.From(the row
// WaitWhileAsync returned); `ok` is the route's success callback. writeAuditEntry and the
// cancellation token have no effect on the answer or the client calls, so they are not modelled.
ghost function RunPrivilegedAdminRequest(kind: CsString, payloadJson: CsString, wait: int, requestId: Guid, waited: Option<PResult>, ok: PResult -> Http): (Http, seq<RouteCall>)
{
  var calls := [Submitted(kind, None), Waited(wait)];
  var result := waited;
  if result.None? then
    (Accepted202(requestId), calls)
  else if result.value.Code == NotFound then
    (NotFound404(result.value.Message), calls)
  else if result.value.Code != OK then
    (Error500(result.value.Message), calls)
  else
    (ok(result.value), calls)
}

// Program.cs:664-672 (3e). `requestId` is SubmitAsync's id; `waited` is
// PrivilegedRequestResult.From(the row WaitWhileAsync returned).
ghost function ReconcileRoute(typeName: CsString, requestId: Guid, waited: Option<PResult>, w: Waits): (Http, seq<RouteCall>)
{
  RunPrivilegedAdminRequest(
    ReconcileTypeKind,
    SerializeReconcilePayload(typeName),
    w.Reconcile,
    requestId,
    waited,
    (result: PResult) => Ok200(ReconciledBody(result.Count, typeName)))
}

// Program.cs:738-755 (3f). `summary` is GetUnreplayedSummaryAsync's row; `actingTenant` is the
// acting principal's tenant_id claim; `requestId` is SubmitAsync's id; `waited` is
// PrivilegedRequestResult.From(the row WaitWhileAsync returned).
ghost function ReplayRoute(id: Guid, summary: Option<Summary>, actingTenant: Option<CsString>, isOperator: bool, requestId: Guid, waited: Option<PResult>, w: Waits): (Http, seq<RouteCall>)
{
  var row := summary;
  if row.None? then
    (NotFound404(Some("No unreplayed DLQ row with id '" + GuidToString(id) + "'")), [])
  else if row.value.TenantId != actingTenant && !(row.value.TenantId.None? && isOperator) then
    (Forbidden403, [])
  else
    RunPrivilegedAdminRequest(
      ReplayDlqKind,
      SerializeReplayPayload(id),
      w.Replay,
      requestId,
      waited,
      (_: PResult) => Ok200(ReplayedBody(id, row.value.SourceTopic)))
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "still running: `202 {requestId}`, and the worker completes it. Reconcile is idempotent, so the request has no expiry;" / "`202 {requestId}`: still pending." (spec §5.2)
lemma P1_AnUnfinishedRequestIsAnswered202WithItsId(typeName: CsString, id: Guid, summary: Summary, actingTenant: Option<CsString>, isOperator: bool, requestId: Guid, w: Waits)
  ensures ReconcileRoute(typeName, requestId, None, w).0 == Accepted202(requestId)
  ensures summary.TenantId == actingTenant ==>
    ReplayRoute(id, Some(summary), actingTenant, isOperator, requestId, None, w).0 == Accepted202(requestId)
{
}

// Citation: "the type is unknown: `404`;" / "`404`: the row is gone or was already replayed (the worker re-checks `Replayed=false`);" (spec §5.2)
lemma P2_AWorkerNotFoundIs404(typeName: CsString, id: Guid, summary: Summary, actingTenant: Option<CsString>, isOperator: bool, requestId: Guid, w: Waits, m: Option<seq<char>>, c: Option<Int32>)
  ensures ReconcileRoute(typeName, requestId, Some(PResult(NotFound, m, c)), w).0 == NotFound404(m)
  ensures summary.TenantId == actingTenant ==>
    ReplayRoute(id, Some(summary), actingTenant, isOperator, requestId, Some(PResult(NotFound, m, c)), w).0 == NotFound404(m)
{
}

// Citation: "any other worker failure: `500 {error}` with the worker's recorded message (the same applies to replay)." (spec §5.2)
lemma P3_AnyOtherWorkerFailureIs500WithItsMessage(typeName: CsString, id: Guid, summary: Summary, actingTenant: Option<CsString>, isOperator: bool, requestId: Guid, w: Waits, r: PResult)
  requires r.Code != OK && r.Code != NotFound
  ensures ReconcileRoute(typeName, requestId, Some(r), w).0 == Error500(r.Message)
  ensures summary.TenantId == actingTenant ==>
    ReplayRoute(id, Some(summary), actingTenant, isOperator, requestId, Some(r), w).0 == Error500(r.Message)
{
}

// Citation: "the worker finished: `200 {reconciledCount, typeName}`, as today;" / "`200`: replayed;" (spec §5.2)
lemma P4_AFinishedRequestIs200(typeName: CsString, id: Guid, summary: Summary, actingTenant: Option<CsString>, isOperator: bool, requestId: Guid, w: Waits, m: Option<seq<char>>, c: Option<Int32>)
  ensures ReconcileRoute(typeName, requestId, Some(PResult(OK, m, c)), w).0 == Ok200(ReconciledBody(c, typeName))
  ensures summary.TenantId == actingTenant ==>
    ReplayRoute(id, Some(summary), actingTenant, isOperator, requestId, Some(PResult(OK, m, c)), w).0 ==
      Ok200(ReplayedBody(id, summary.SourceTopic))
{
}

// Citation: "The api keeps every check it runs today: Operator, the active tenant, the row's `TenantId` against the caller's tenant, and `Replayed=false`. It reads those columns without `MessageValue`." (spec §5.2) and "await _requests.DidNotReceive().InsertAsync(" (plan T8 S1c, `Replay_ARowOfAnotherTenant_Returns403_AndNothingIsQueued`)
lemma P5_TheReplayChecksRunBeforeAnythingIsQueued(id: Guid, summary: Option<Summary>, actingTenant: Option<CsString>, isOperator: bool, requestId: Guid, waited: Option<PResult>, w: Waits)
  ensures summary.None? ==>
    ReplayRoute(id, summary, actingTenant, isOperator, requestId, waited, w).0.NotFound404? &&
    ReplayRoute(id, summary, actingTenant, isOperator, requestId, waited, w).1 == []
  ensures summary.Some? && summary.value.TenantId != actingTenant && !(summary.value.TenantId.None? && isOperator) ==>
    ReplayRoute(id, summary, actingTenant, isOperator, requestId, waited, w) == (Forbidden403, [])
{
}

// Citation: "Reconcile is idempotent, so the request has no expiry;" / "The api waits up to 60 s:" / "It queues the replay and waits up to 30 s:" (spec §5.2) and "_inserted.Value.ExpiresAfter.Should().BeNull("a reconcile is idempotent, so it never expires");" (plan T8 S1c, `Reconcile_TheWorkerFinished_Returns200WithTheCount`)
lemma P6_AdminRequestsNeverExpire(typeName: CsString, id: Guid, summary: Summary, actingTenant: Option<CsString>, isOperator: bool, requestId: Guid, waited: Option<PResult>, w: Waits)
  ensures ReconcileRoute(typeName, requestId, waited, w).1 == [Submitted("ReconcileType", None), Waited(w.Reconcile)]
  ensures summary.TenantId == actingTenant ==>
    ReplayRoute(id, Some(summary), actingTenant, isOperator, requestId, waited, w).1 == [Submitted("ReplayDlq", None), Waited(w.Replay)]
{
}
