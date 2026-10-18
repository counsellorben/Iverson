// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T7 S3
// Source: Iverson.Server/Iverson.Api/PrivilegedRequests/PrivilegedRequestWorker.cs `PollOnceAsync`, `RunClaimedAsync`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/PrivilegedRequestWorker.PollOnceAsync.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

newtype Int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

type Guid(==)

// PrivilegedRequestResult.ToJson(): JsonSerializer.Serialize(this, SchemaRegistry.JsonOptions).
function ResultToJson(r: PResult): CsString

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// PrivilegedRequestContracts.cs:34
// public sealed record PrivilegedRequestResult(StatusCode Code, string? Message = null, int? Count = null)
// Message is seq<char> (shared-vocabulary rule for header-filled strings), so a literal needs no length proof.
datatype PResult = PResult(Code: StatusCode, Message: Option<seq<char>>, Count: Option<Int32>)

// PrivilegedRequestRow (Iverson.Sql), the fields the worker reads.
datatype Req = Req(Id: Guid, Kind: CsString)

// The side effects of one poll, in order.
//   FailAbandoned  = requests.FailAbandonedAsync(AbandonedAfter, Abandoned.ToJson())
//   DeleteFinished = requests.DeleteFinishedOlderThanAsync(FinishedRetention)
//   Ran            = executor.RunAsync(request, ct)
//   Completed      = requests.CompleteAsync(request.Id, status, result.ToJson())
datatype WEffect = FailAbandoned(age: int, resultJson: CsString) | DeleteFinished(age: int) | Ran(id: Guid) | Completed(id: Guid, status: seq<char>, resultJson: CsString)

// TimeSpan values, in ticks.
const OneHour: int := 36_000_000_000
const OneDay: int := 864_000_000_000

// PrivilegedRequestWorker.cs:22-23, 29
const CleanupInterval: int := OneHour
const FinishedRetention: int := OneDay
const AbandonedAfter: int := OneHour

// PrivilegedRequestWorker.cs:30-31
const Abandoned: PResult := PResult(Unavailable, Some("The worker stopped before finishing this request."), None)

// PrivilegedRequestStatus (Iverson.Sql/PrivilegedRequestRow.cs:12-13)
const Succeeded: seq<char> := "succeeded"
const Failed: seq<char> := "failed"

// PrivilegedRequestWorker.cs:78-94  RunClaimedAsync
// executor.RunAsync catches every exception (PrivilegedRequestExecutor.cs:45-59), so it is the
// total oracle `results`. CompleteAsync's exception is caught and logged: the call still happened,
// so its effect is recorded either way.
function RunClaimed(request: Req, results: Guid -> PResult): seq<WEffect>
{
  var result := results(request.Id);
  [Ran(request.Id)] +
  [Completed(request.Id, if result.Code == OK then Succeeded else Failed, ResultToJson(result))]
}

// PrivilegedRequestWorker.cs:67-73  the claim loop, one iteration per listed request.
function ClaimLoop(listed: seq<Req>, claims: Guid -> bool, results: Guid -> PResult): seq<WEffect>
  decreases |listed|
{
  if |listed| == 0 then []
  else
    var request := listed[0];
    if !claims(request.Id) then
      ClaimLoop(listed[1..], claims, results)                              // continue
    else
      RunClaimed(request, results) + ClaimLoop(listed[1..], claims, results)
}

// PrivilegedRequestWorker.cs:54-74  PollOnceAsync
// `listed` is ListClaimableAsync(ClaimBatchSize)'s result; `now` is DateTimeOffset.UtcNow (one
// instant per poll); `lastCleanup` is _lastCleanup. Returns the new _lastCleanup and the effects.
function PollOnce(lastCleanup: int, now: int, listed: seq<Req>, claims: Guid -> bool, results: Guid -> PResult): (int, seq<WEffect>)
{
  var (lastCleanup', cleanup) :=
    if now - lastCleanup >= CleanupInterval then
      (now, [FailAbandoned(AbandonedAfter, ResultToJson(Abandoned))] + [DeleteFinished(FinishedRetention)])
    else
      (lastCleanup, []);
  (lastCleanup', cleanup + ClaimLoop(listed, claims, results))
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma ClaimLoop_RanOnlyClaimed(listed: seq<Req>, claims: Guid -> bool, results: Guid -> PResult, id: Guid)
  requires Ran(id) in ClaimLoop(listed, claims, results)
  ensures exists r :: r in listed && r.Id == id && claims(r.Id)
  decreases |listed|
{
  var request := listed[0];
  if claims(request.Id) && Ran(id) in RunClaimed(request, results) {
    assert request in listed;
  } else {
    ClaimLoop_RanOnlyClaimed(listed[1..], claims, results, id);
    var r :| r in listed[1..] && r.Id == id && claims(r.Id);
    assert r in listed;
  }
}

lemma ClaimLoop_CompletesClaimed(listed: seq<Req>, claims: Guid -> bool, results: Guid -> PResult, r: Req)
  requires r in listed && claims(r.Id)
  ensures Completed(r.Id, if results(r.Id).Code == OK then "succeeded" else "failed", ResultToJson(results(r.Id)))
    in ClaimLoop(listed, claims, results)
  decreases |listed|
{
  var request := listed[0];
  if request == r {
    var c := Completed(r.Id, if results(r.Id).Code == OK then "succeeded" else "failed", ResultToJson(results(r.Id)));
    assert RunClaimed(request, results)[1] == c;
    assert c in RunClaimed(request, results);
  } else {
    assert r in listed[1..];
    ClaimLoop_CompletesClaimed(listed[1..], claims, results, r);
  }
}

lemma ClaimLoop_NoCleanup(listed: seq<Req>, claims: Guid -> bool, results: Guid -> PResult)
  ensures forall e :: e in ClaimLoop(listed, claims, results) ==> !e.FailAbandoned? && !e.DeleteFinished?
  decreases |listed|
{
  if |listed| > 0 {
    ClaimLoop_NoCleanup(listed[1..], claims, results);
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "Zero rows means it lost the race." (spec §5.1, Claiming) and "await _executor.DidNotReceive().RunAsync(Arg.Is(lost), Arg.Any<CancellationToken>());" (plan T7 S1e, `PollOnce_RunsOnlyTheRequestsItClaims`)
lemma P1_OnlyClaimedRequestsRun(lastCleanup: int, now: int, listed: seq<Req>, claims: Guid -> bool, results: Guid -> PResult)
  ensures forall id :: Ran(id) in PollOnce(lastCleanup, now, listed, claims, results).1 ==>
    exists r :: r in listed && r.Id == id && claims(r.Id)
{
  var E := PollOnce(lastCleanup, now, listed, claims, results).1;
  var L := ClaimLoop(listed, claims, results);
  forall id | Ran(id) in E
    ensures exists r :: r in listed && r.Id == id && claims(r.Id)
  {
    assert Ran(id) in L;
    ClaimLoop_RanOnlyClaimed(listed, claims, results, id);
  }
}

// Citation: "status.Should().Be(PrivilegedRequestStatus.Failed);" (plan T7 S1e, `AFailedOutcome_IsRecordedAsFailed_WithItsResult`) and "_completed[won.Id].Status.Should().Be(PrivilegedRequestStatus.Succeeded);" (plan T7 S1e, `PollOnce_RunsOnlyTheRequestsItClaims`)
lemma P2_EachClaimedRunIsRecordedSucceededOnlyWhenOk(lastCleanup: int, now: int, listed: seq<Req>, claims: Guid -> bool, results: Guid -> PResult)
  requires forall i, j :: 0 <= i < j < |listed| ==> listed[i].Id != listed[j].Id
  ensures forall r :: r in listed && claims(r.Id) ==>
    Completed(r.Id, if results(r.Id).Code == OK then "succeeded" else "failed", ResultToJson(results(r.Id)))
      in PollOnce(lastCleanup, now, listed, claims, results).1
{
  var E := PollOnce(lastCleanup, now, listed, claims, results).1;
  var L := ClaimLoop(listed, claims, results);
  assert E[|E| - |L|..] == L;
  forall r | r in listed && claims(r.Id)
    ensures Completed(r.Id, if results(r.Id).Code == OK then "succeeded" else "failed", ResultToJson(results(r.Id))) in E
  {
    ClaimLoop_CompletesClaimed(listed, claims, results, r);
  }
}

// Citation: "**Cleanup:** finished rows older than one day are deleted. A row still `running` more than one hour after it was claimed is marked failed ("the worker stopped before finishing this request"), so a worker that dies mid-request never leaves a request pending forever." (spec §5.1)
lemma P3_CleanupRunsAtMostHourly(lastCleanup: int, now: int, listed: seq<Req>, claims: Guid -> bool, results: Guid -> PResult)
  ensures var next := PollOnce(lastCleanup, now, listed, claims, results).0;
          var E := PollOnce(lastCleanup, now, listed, claims, results).1;
    (now - lastCleanup >= OneHour ==>
      |E| >= 2 &&
      E[0] == FailAbandoned(OneHour, ResultToJson(PResult(Unavailable, Some("The worker stopped before finishing this request."), None))) &&
      E[1] == DeleteFinished(OneDay) && next == now) &&
    (now - lastCleanup < OneHour ==>
      next == lastCleanup && forall e :: e in E ==> !e.FailAbandoned? && !e.DeleteFinished?)
{
  ClaimLoop_NoCleanup(listed, claims, results);
}
