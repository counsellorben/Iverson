// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T7 S3
// Source: Iverson.Server/Iverson.Api/PrivilegedRequests/QueuedSchemaRegistrationExecutor.cs `ExecuteAsync`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/QueuedSchemaRegistrationExecutor.ExecuteAsync.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary

// A C# string (UTF-16 code units under --unicode-char false); its Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

newtype Int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

datatype Option<T> = None | Some(value: T)

// Grpc.Core.StatusCode
datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

// SchemaDescriptor, keeping only the fields this code reads (OwnerTenantId, for requestedBy)
// and the type's name. Every other field is omitted: no header of this candidate names it.
datatype Desc = Desc(TypeName: CsString, OwnerTenantId: Option<CsString>)

// ---------------------------------------------------------------------------
// Approved axiom: JsonSerializer.Serialize(new RegisterSchemaPayload(descriptors,
// changedTemplateTypes), SchemaRegistry.JsonOptions).
function SerializeRegisterPayload(ds: seq<Desc>, changed: seq<CsString>): CsString

// ---------------------------------------------------------------------------
// This candidate's model

// PrivilegedRequestResult(StatusCode Code, string? Message = null, int? Count = null)
datatype PResult = PResult(Code: StatusCode, Message: Option<seq<char>>, Count: Option<Int32>)

// Iverson.Sql PrivilegedRequestStatus: the five string constants the column holds.
datatype ReqStatus = Pending | Running | Succeeded | Failed | Expired

// PrivilegedRequestRow, as this code reads it: its Status, and Result, which stands for
// PrivilegedRequestResult.From(row) (None when ResultJson is null; the JSON round trip is not
// modelled).
datatype ReqRow = ReqRow(Status: ReqStatus, Result: Option<PResult>)

// PrivilegedRequestWaits, each TimeSpan in ticks.
datatype Waits = Waits(RegisterClaim: int, RegisterRun: int, Reconcile: int, Replay: int, PollInterval: int)

// The answers, in call order, of:
//   afterClaim  requests.WaitWhileAsync(id, Pending, waits.RegisterClaim, ct)
//   expired     requests.ExpireIfPendingAsync(id)
//   reread      requests.ReadAsync(id)
//   afterRun    requests.WaitWhileAsync(id, Running, waits.RegisterRun, ct)
//   reloadOk    registry.LoadAsync(ct) completed (false: it threw)
datatype Answers = Answers(afterClaim: ReqRow, expired: bool, reread: ReqRow, afterRun: ReqRow, reloadOk: bool)

// The external calls, recorded in order as they are made.
datatype Call = Submit(expiresAfter: Option<int>) | WaitClaim(timeout: int) | Expire | Reread | WaitRun(timeout: int) | Reload

// How ExecuteAsync ends: it returned, it threw an RpcException(Status(code, detail)), or it
// threw anything else (a failed reload, or the NullReferenceException of a null From on a
// failed row).
datatype Outcome = Done | Rpc(code: StatusCode, detail: seq<char>) | Fault

// QueuedSchemaRegistrationExecutor.cs:20
const NotPickedUp: seq<char> := "The worker did not pick the registration up; retry."

// descriptors.Select(d => d.OwnerTenantId).FirstOrDefault()
function FirstOwner(ds: seq<Desc>): Option<CsString>
{
  if |ds| == 0 then None else ds[0].OwnerTenantId
}

// ---------------------------------------------------------------------------
// QueuedSchemaRegistrationExecutor.cs:22-49 ExecuteAsync, up to the run wait
function Execute(ds: seq<Desc>, changed: seq<CsString>, w: Waits, a: Answers): (Outcome, seq<Call>)
{
  // :27 var waits = requests.Waits;
  var waits := w;
  // :28-29 var payload = JsonSerializer.Serialize(new RegisterSchemaPayload(...), ...);
  var payload := SerializeRegisterPayload(ds, changed);
  // :33-37 var id = await requests.SubmitAsync(RegisterSchema, payload, requestedBy: ...,
  //        expiresAfter: waits.RegisterClaim);
  var requestedBy := FirstOwner(ds);
  var calls := [Submit(Some(waits.RegisterClaim))];
  // :39-40 var request = await requests.WaitWhileAsync(id, Pending, waits.RegisterClaim, ct);
  var calls := calls + [WaitClaim(waits.RegisterClaim)];
  var request := a.afterClaim;
  // :42 if (request.Status == Pending)
  if request.Status == Pending then
    // :46 if (await requests.ExpireIfPendingAsync(id))
    var calls := calls + [Expire];
    if a.expired then
      // :47 throw new RpcException(new Status(StatusCode.Unavailable, NotPickedUp));
      (Rpc(Unavailable, NotPickedUp), calls)
    else
      // :48 request = await requests.ReadAsync(id);
      var calls := calls + [Reread];
      var request := a.reread;
      AfterClaim(request, calls, waits, a)
  else
    AfterClaim(request, calls, waits, a)
}

// QueuedSchemaRegistrationExecutor.cs:51-69, the rest of ExecuteAsync
function AfterClaim(request: ReqRow, calls: seq<Call>, waits: Waits, a: Answers): (Outcome, seq<Call>)
{
  // :51-53 if (request.Status == Running)
  //          request = await requests.WaitWhileAsync(id, Running, waits.RegisterRun, ct);
  var (request, calls) :=
    if request.Status == Running then (a.afterRun, calls + [WaitRun(waits.RegisterRun)])
    else (request, calls);
  // :55 switch (request.Status)
  match request.Status
  // :57-59 case Succeeded: await registry.LoadAsync(ct); return;
  case Succeeded =>
    (if a.reloadOk then Done else Fault, calls + [Reload])
  // :60-63 case Running: throw new RpcException(new Status(DeadlineExceeded, "..."));
  case Running =>
    (Rpc(DeadlineExceeded,
         "The registration is still being applied. It completes in the worker; registering " +
         "again once it has is idempotent."), calls)
  // :64-66 case Failed:
  //          var result = PrivilegedRequestResult.From(request)!;
  //          throw new RpcException(new Status(result.Code, result.Message ?? string.Empty));
  case Failed =>
    (match request.Result
     // result.Code on a null result: NullReferenceException
     case None => (Fault, calls)
     case Some(result) =>
       (Rpc(result.Code, if result.Message.Some? then result.Message.value else ""), calls))
  // :67-68 default: throw new RpcException(new Status(StatusCode.Unavailable, NotPickedUp));
  case _ =>
    (Rpc(Unavailable, NotPickedUp), calls)
}

// ---------------------------------------------------------------------------
// Properties

// Citation: "On `succeeded`, the api reloads its own registry (`LoadAsync`) before answering, so the caller's next write on that replica sees the type." (spec §5.2)
lemma P1_SuccessIsAnsweredOnlyAfterTheApiReloads(ds: seq<Desc>, changed: seq<CsString>, w: Waits, a: Answers)
  ensures Execute(ds, changed, w, a).0 == Done <==> (Reload in Execute(ds, changed, w, a).1 && a.reloadOk)
  ensures Reload in Execute(ds, changed, w, a).1 ==>
    Execute(ds, changed, w, a).1[|Execute(ds, changed, w, a).1| - 1] == Reload
  ensures Reload in Execute(ds, changed, w, a).1 ==>
    a.afterClaim.Status == Succeeded ||
    (a.afterClaim.Status == Running && a.afterRun.Status == Succeeded) ||
    (a.afterClaim.Status == Pending && !a.expired &&
      (a.reread.Status == Succeeded || (a.reread.Status == Running && a.afterRun.Status == Succeeded)))
{
}

// Citation: "**15 s** for the worker to claim the request. If the api then changes `pending` to `expired`, the worker will never run it, and the caller gets `Unavailable` ("the worker did not pick the registration up; retry")." (spec §5.2)
lemma P2_ARequestNeverClaimedIsExpiredAndUnavailable(ds: seq<Desc>, changed: seq<CsString>, w: Waits, a: Answers)
  requires a.afterClaim.Status == Pending && a.expired
  ensures Execute(ds, changed, w, a).0 == Rpc(Unavailable, "The worker did not pick the registration up; retry.")
  ensures Reload !in Execute(ds, changed, w, a).1
{
}

// Citation: "If the claimed request still hasn't finished after 10 s, the caller gets `DeadlineExceeded` ("registration is still being applied") and the request completes in the worker." (spec §5.2)
lemma P3_AClaimedRequestStillRunningIsDeadlineExceeded(ds: seq<Desc>, changed: seq<CsString>, w: Waits, a: Answers)
  requires a.afterClaim.Status == Running || (a.afterClaim.Status == Pending && !a.expired && a.reread.Status == Running)
  requires a.afterRun.Status == Running
  ensures Execute(ds, changed, w, a).0.Rpc? && Execute(ds, changed, w, a).0.code == DeadlineExceeded
  ensures Reload !in Execute(ds, changed, w, a).1
{
}

// Citation: "On `failed`, the api returns the stored status code and message." (spec §5.2)
lemma P4_AFailedRequestAnswersWithItsStoredStatusAndMessage(ds: seq<Desc>, changed: seq<CsString>, w: Waits, a: Answers, r: PResult)
  requires (a.afterClaim.Status == Failed && a.afterClaim.Result == Some(r)) ||
           (a.afterClaim.Status == Running && a.afterRun.Status == Failed && a.afterRun.Result == Some(r))
  ensures Execute(ds, changed, w, a).0 == Rpc(r.Code, if r.Message.Some? then r.Message.value else "")
{
}

// Citation: "**Expiry:** `ExpiresAt` is `RequestedAt + 15 s` for RegisterSchema" (spec §5.1) and "If the worker had already claimed it, a further **10 s**." / "This 25 s maximum is below every edge default on the gRPC path" (spec §5.2)
lemma P5_TheRequestExpiresWithTheClaimWait_AndTheWaitsAreBounded(ds: seq<Desc>, changed: seq<CsString>, w: Waits, a: Answers)
  ensures var C := Execute(ds, changed, w, a).1;
    |C| >= 2 && C[0] == Submit(Some(w.RegisterClaim)) && C[1] == WaitClaim(w.RegisterClaim) &&
    (forall i :: 1 < i < |C| ==> !C[i].WaitClaim?) &&
    (Expire in C ==> a.afterClaim.Status == Pending) &&
    (forall c :: c in C && c.WaitRun? ==> c.timeout == w.RegisterRun) &&
    (forall i, j :: 0 <= i < j < |C| && C[i].WaitRun? ==> !C[j].WaitRun?)
{
}
