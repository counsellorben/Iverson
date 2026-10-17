// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T7 S3, T8 S3
// Source: Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestExecutor.cs `RunAsync`, `RegisterSchemaAsync`, `ReconcileTypeAsync`, `ReplayDlqAsync`, `Deserialize`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/PrivilegedRequestExecutor.RunAsync.dfy

// ---------------------------------------------------------------------------
// Approved axiom: System.Guid, an opaque value type with equality.
type Guid(==)

// ---------------------------------------------------------------------------
// Shared vocabulary

// A C# string (UTF-16 code units under --unicode-char false); its Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

newtype Int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

datatype Option<T> = None | Some(value: T)

// Grpc.Core.StatusCode
datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

// ---------------------------------------------------------------------------
// This candidate's model

// The three exception classes RunAsync's catch clauses tell apart:
//   RpcEx    -- Grpc.Core.RpcException (StatusCode, Status.Detail)
//   Canceled -- OperationCanceledException (incl. TaskCanceledException)
//   OtherEx  -- every other exception (InvalidOperationException, JsonException, ...)
datatype Exn = RpcEx(code: StatusCode, detail: seq<char>) | Canceled | OtherEx

// The outcome of one external call: it completed with a value, or it threw.
datatype Step<T> = Did(value: T) | Threw(e: Exn)

// The deserialised request payload (PrivilegedRequestContracts.cs:16-28). Only the fields this
// code reads are kept: ReconcileTypePayload.TypeName and ReplayDlqPayload.Id.
datatype Payload = RegisterP | ReconcileP(TypeName: CsString) | ReplayP(Id: Guid)

// Iverson.Sql DlqReplayRow: the three fields ProduceAsync is given.
datatype DlqRow = DlqRow(SourceTopic: CsString, MessageKey: CsString, MessageValue: CsString)

// Each field is the outcome of one external call:
//   load             SchemaRegistry.LoadAsync(ct)
//   payload          JsonSerializer.Deserialize<T>(request.Payload, ...)
//   registrationLock IPrivilegedRequestLocks.AcquireRegistrationAsync(ct)
//   typeLock         IPrivilegedRequestLocks.AcquireTypeAsync(typeName, ct)
//   apply            SchemaRegistrationApplier.ExecuteAsync(...)
//   reconcile        ReconciliationService.ReconcileTypeAsync(typeName, ct)   (Task<int?>)
//   dlqRow           IDlqRepository.GetUnreplayedByIdAsync(id)               (Task<DlqReplayRow?>)
//   produce          IEventProducer.ProduceAsync(...)
//   mark             IDlqRepository.MarkReplayedAsync(id)
//   cancelled        ct.IsCancellationRequested, constant for the run
//   guidText         Guid.ToString() inside the replay NotFound message's interpolation
//                    (deviation: added field; Guid formatting is an external call)
datatype Oracle = Oracle(load: Step<()>, payload: Step<Payload>, registrationLock: Step<()>, typeLock: Step<()>, apply: Step<()>, reconcile: Step<Option<Int32>>, dlqRow: Step<Option<DlqRow>>, produce: Step<()>, mark: Step<()>, cancelled: bool, guidText: Guid -> seq<char>)

// Each effect is recorded when its call completes.
datatype Effect = Loaded | RegistrationLocked | TypeLocked(typeName: CsString) | Applied | Reconciled(typeName: CsString) | Produced | Marked

// PrivilegedRequestResult(StatusCode Code, string? Message = null, int? Count = null)
datatype PResult = PResult(Code: StatusCode, Message: Option<seq<char>>, Count: Option<Int32>)

// A private helper's outcome inside RunAsync's try block: it returned a result, or threw.
datatype Outcome = Returned(r: PResult) | Raised(e: Exn)

// PrivilegedRequestResult.Ok(int? count = null) => new(StatusCode.OK, null, count)
function Ok(count: Option<Int32>): PResult
{
  PResult(OK, None, count)
}

// ---------------------------------------------------------------------------
// PrivilegedRequestExecutor.cs:31-60 RunAsync
function RunAsync(kind: seq<char>, o: Oracle): (PResult, seq<Effect>)
{
  var body := TryBlock(kind, o);
  var effects := body.1;
  match body.0
  // try { ... return ...; }
  case Returned(r) => (r, effects)
  // catch (RpcException ex) { return new(ex.StatusCode, ex.Status.Detail); }
  case Raised(RpcEx(code, detail)) => (PResult(code, Some(detail), None), effects)
  // catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Unavailable ...; }
  case Raised(Canceled) =>
    if o.cancelled then
      (PResult(Unavailable, Some("The worker stopped before the request finished; retry."), None), effects)
    else
      // the filter is false: the OperationCanceledException falls to catch (Exception)
      (PResult(Internal, Some("The request failed in the worker; the worker log has the details."), None), effects)
  // catch (Exception ex) { logger.LogError(...); return Internal ...; }
  case Raised(OtherEx) =>
    (PResult(Internal, Some("The request failed in the worker; the worker log has the details."), None), effects)
}

// PrivilegedRequestExecutor.cs:33-44, the try block
function TryBlock(kind: seq<char>, o: Oracle): (Outcome, seq<Effect>)
{
  // await registry.LoadAsync(ct);
  match o.load
  case Threw(e) => (Raised(e), [])
  case Did(_) =>
    // return request.Kind switch { ... };   (string switch: ordinal equality)
    var arm :=
      if kind == "RegisterSchema" then RegisterSchemaAsync(o)
      else if kind == "ReconcileType" then ReconcileTypeAsync(o)
      else if kind == "ReplayDlq" then ReplayDlqAsync(o)
      // _ => throw new InvalidOperationException(...)
      else (Raised(OtherEx), []);
    (arm.0, [Loaded] + arm.1)
}

// PrivilegedRequestExecutor.cs:64-71 RegisterSchemaAsync
function RegisterSchemaAsync(o: Oracle): (Outcome, seq<Effect>)
{
  // var payload = Deserialize<RegisterSchemaPayload>(request);   (:108-110)
  match o.payload
  case Did(RegisterP) =>
    // await using var registrationLock = await locks.AcquireRegistrationAsync(ct);
    (match o.registrationLock
     case Threw(e) => (Raised(e), [])
     case Did(_) =>
       // await registration.ExecuteAsync(payload.Descriptors, payload.ChangedTemplateTypes, ct);
       match o.apply
       case Threw(e) => (Raised(e), [RegistrationLocked])
       // return PrivilegedRequestResult.Ok();
       case Did(_) => (Returned(Ok(None)), [RegistrationLocked, Applied]))
  // Deserialize threw (JsonException), returned null (InvalidOperationException), or the payload
  // is of another kind: OtherEx.
  case _ => (Raised(OtherEx), [])
}

// PrivilegedRequestExecutor.cs:77-91 ReconcileTypeAsync
function ReconcileTypeAsync(o: Oracle): (Outcome, seq<Effect>)
{
  // var payload = Deserialize<ReconcileTypePayload>(request);   (:108-110)
  match o.payload
  case Did(ReconcileP(t)) =>
    // await using var typeLock = await locks.AcquireTypeAsync(payload.TypeName, ct);
    (match o.typeLock
     case Threw(e) => (Raised(e), [])
     case Did(_) =>
       // var count = await reconciliation.ReconcileTypeAsync(payload.TypeName, ct);
       match o.reconcile
       case Threw(e) => (Raised(e), [TypeLocked(t)])
       case Did(count) =>
         var eff := [TypeLocked(t), Reconciled(t)];
         // ct.ThrowIfCancellationRequested();
         if o.cancelled then (Raised(Canceled), eff)
         else
           // return count is null ? NotFound(...) : Ok(count);
           match count
           case None => (Returned(PResult(NotFound, Some("No schema registered for '" + t + "'"), None)), eff)
           case Some(n) => (Returned(Ok(Some(n))), eff))
  case _ => (Raised(OtherEx), [])
}

// PrivilegedRequestExecutor.cs:95-106 ReplayDlqAsync
function ReplayDlqAsync(o: Oracle): (Outcome, seq<Effect>)
{
  // var payload = Deserialize<ReplayDlqPayload>(request);   (:108-110)
  match o.payload
  case Did(ReplayP(id)) =>
    // var row = await dlq.GetUnreplayedByIdAsync(payload.Id);
    (match o.dlqRow
     case Threw(e) => (Raised(e), [])
     case Did(row) =>
       // if (row is null) return new(StatusCode.NotFound, $"No unreplayed DLQ row with id '{payload.Id}'");
       if row.None? then
         (Returned(PResult(NotFound, Some("No unreplayed DLQ row with id '" + o.guidText(id) + "'"), None)), [])
       else
         // await events.ProduceAsync(row.SourceTopic, row.MessageKey, row.MessageValue);
         match o.produce
         case Threw(e) => (Raised(e), [])
         case Did(_) =>
           // await dlq.MarkReplayedAsync(payload.Id);
           match o.mark
           case Threw(e) => (Raised(e), [Produced])
           // return PrivilegedRequestResult.Ok();
           case Did(_) => (Returned(Ok(None)), [Produced, Marked]))
  case _ => (Raised(OtherEx), [])
}

// ---------------------------------------------------------------------------
// Properties

// Citation: "result.Code.Should().Be(StatusCode.PermissionDenied);" / "result.Message.Should().Be("Type 'Probe' is registered to another tenant and cannot be re-registered here.");" (plan T7 S1d, `ARefusal_IsRecordedWithItsStatusAndMessage`, generalised)
lemma P1_ARefusalIsRecordedWithItsStatusAndMessage(o: Oracle, code: StatusCode, detail: seq<char>)
  requires o.load == Did(()) && o.payload == Did(RegisterP) && o.registrationLock == Did(())
  requires o.apply == Threw(RpcEx(code, detail))
  ensures RunAsync("RegisterSchema", o).0 == PResult(code, Some(detail), None)
{
}

// Citation: "**Errors:** an unexpected exception records `Internal` with a generic message; the details go to the log." (spec §5.1)
lemma P2_AnUnexpectedFailureIsInternalWithAGenericMessage(kind: seq<char>, o: Oracle)
  requires o.load == Threw(OtherEx) ||
           (o.load == Did(()) && kind != "RegisterSchema" && kind != "ReconcileType" && kind != "ReplayDlq") ||
           (kind == "RegisterSchema" && o.load == Did(()) && o.payload == Did(RegisterP) &&
            o.registrationLock == Did(()) && o.apply == Threw(OtherEx))
  ensures RunAsync(kind, o).0 == PResult(Internal, Some("The request failed in the worker; the worker log has the details."), None)
{
}

// Citation: "result.Code.Should().Be(StatusCode.Unavailable);" (plan T8 S1a, `ReconcileType_StoppedByWorkerShutdown_IsUnavailable_NotAPartialCount`, and T7 S1d, `StoppedByWorkerShutdown_IsRecordedAsUnavailable`)
lemma P3_StoppingTheWorkerIsUnavailableNeverAPartialResult(kind: seq<char>, o: Oracle)
  requires o.cancelled
  requires o.load == Threw(Canceled) ||
           (kind == "RegisterSchema" && o.load == Did(()) && o.payload == Did(RegisterP) && o.registrationLock == Threw(Canceled)) ||
           (kind == "ReconcileType" && o.load == Did(()) && o.payload.Did? && o.payload.value.ReconcileP? &&
            o.typeLock == Did(()) && o.reconcile.Did?)
  ensures RunAsync(kind, o).0.Code == Unavailable
{
}

// Citation: "`RegisterSchema` takes one global registration lock, which serialises it across worker replicas." / "`ReconcileType` takes a per-type lock, so a long reconcile never blocks registration." / "`ReplayDlq` takes none." (spec §5.1)
lemma P4_EachKindTakesOnlyItsOwnLock(kind: seq<char>, o: Oracle)
  ensures var E := RunAsync(kind, o).1;
    (kind == "RegisterSchema" ==>
      (forall e :: e in E ==> !e.TypeLocked?) &&
      (Applied in E ==> exists i, j :: 0 <= i < j < |E| && E[i] == RegistrationLocked && E[j] == Applied)) &&
    (kind == "ReconcileType" ==>
      RegistrationLocked !in E &&
      (forall j, t :: 0 <= j < |E| && E[j] == Reconciled(t) ==> exists i :: 0 <= i < j && E[i] == TypeLocked(t))) &&
    (kind == "ReplayDlq" ==> RegistrationLocked !in E && forall e :: e in E ==> !e.TypeLocked?)
{
  var E := RunAsync(kind, o).1;
  if kind == "RegisterSchema" && Applied in E {
    assert E == [Loaded, RegistrationLocked, Applied];
    assert E[1] == RegistrationLocked && E[2] == Applied;
  }
  if kind == "ReconcileType" {
    if o.load.Did? && o.payload.Did? && o.payload.value.ReconcileP? && o.typeLock.Did? && o.reconcile.Did? {
      var tn := o.payload.value.TypeName;
      assert E == [Loaded, TypeLocked(tn), Reconciled(tn)];
      forall j, t | 0 <= j < |E| && E[j] == Reconciled(t)
        ensures exists i :: 0 <= i < j && E[i] == TypeLocked(t)
      {
        assert j == 2 && t == tn;
        assert E[1] == TypeLocked(t);
      }
    }
  }
}

// Citation: "**Registry:** the worker reloads its registry (`LoadAsync`) before running a request, so a type registered moments ago resolves." (spec §5.1)
lemma P5_TheRegistryIsReloadedBeforeAnything(kind: seq<char>, o: Oracle)
  ensures RunAsync(kind, o).1 != [] ==> RunAsync(kind, o).1[0] == Loaded
  ensures o.load.Threw? ==> RunAsync(kind, o).1 == []
{
}

// Citation: "`404`: the row is gone or was already replayed (the worker re-checks `Replayed=false`);" / "The worker produces the message and marks the row replayed. That is at-least-once, as today." (spec §5.2)
lemma P6_AReplayRechecksTheRowThenProducesBeforeMarking(o: Oracle, id: Guid)
  requires o.load == Did(()) && o.payload == Did(ReplayP(id))
  ensures o.dlqRow == Did(None) ==>
    RunAsync("ReplayDlq", o).0.Code == NotFound &&
    Produced !in RunAsync("ReplayDlq", o).1 && Marked !in RunAsync("ReplayDlq", o).1
  ensures Marked in RunAsync("ReplayDlq", o).1 ==>
    exists i, j :: 0 <= i < j < |RunAsync("ReplayDlq", o).1| &&
      RunAsync("ReplayDlq", o).1[i] == Produced && RunAsync("ReplayDlq", o).1[j] == Marked
{
  var E := RunAsync("ReplayDlq", o).1;
  if Marked in E {
    assert E == [Loaded, Produced, Marked];
    assert E[1] == Produced && E[2] == Marked;
  }
}

// Citation: "the worker finished: `200 {reconciledCount, typeName}`, as today;" / "the type is unknown: `404`;" (spec §5.2) and "result.Message.Should().Be("No schema registered for 'NoSuchType'");" (plan T8 S1a, `ReconcileType_AnUnknownType_IsNotFound`)
lemma P7_AReconcileRecordsItsCountOrNotFound(o: Oracle, t: CsString)
  requires o.load == Did(()) && o.payload == Did(ReconcileP(t)) && o.typeLock == Did(()) && !o.cancelled
  ensures o.reconcile == Did(None) ==>
    RunAsync("ReconcileType", o).0 == PResult(NotFound, Some("No schema registered for '" + t + "'"), None)
  ensures forall n: Int32 :: o.reconcile == Did(Some(n)) ==> RunAsync("ReconcileType", o).0 == PResult(OK, None, Some(n))
{
}
