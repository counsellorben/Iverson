// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T7 S3
// Source: Iverson.Server/Iverson.Sql/PrivilegedRequestRepository.cs `InsertAsync`, `ExpireIfPendingAsync`, `ListClaimableAsync`, `TryClaimAsync`, `FailAbandonedAsync`, `DeleteFinishedOlderThanAsync`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/PrivilegedRequestRepository.Transitions.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary (only the names this candidate uses)
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

// ---------------------------------------------------------------------------
// Approved axioms
// ---------------------------------------------------------------------------

type Guid(==)

// ORDER BY "RequestedAt" LIMIT @Limit (PrivilegedRequestRepository.cs:58-59).
function {:axiom} OrderByRequestedAtLimit(rows: seq<Req>, limit: int): seq<Req>
  ensures forall r :: r in OrderByRequestedAtLimit(rows, limit) ==> r in rows

// ---------------------------------------------------------------------------
// Row model (PrivilegedRequestRow.cs PrivilegedRequestStatus; PrivilegedRequestSchema.cs columns)
// ---------------------------------------------------------------------------

// 'pending' | 'running' | 'succeeded' | 'failed' | 'expired' (PrivilegedRequestRow.cs:10-14).
datatype ReqStatus = Pending | Running | Succeeded | Failed | Expired

// Columns the statements read or write. Kind, Payload, RequestedBy and CompletedAt are not
// named by any property and are omitted. Instants are ints (the statement's now()).
datatype Req = Req(Id: Guid, Status: ReqStatus, RequestedAt: int, ExpiresAt: Option<int>,
                   ClaimedAt: Option<int>, ResultJson: Option<CsString>)

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// InsertAsync (cs:16-30). Every now() in one statement is the same instant.
// `now() + @ExpiresAfterSeconds * interval '1 second'`: a NULL parameter gives NULL.
// The unnamed nullable columns (ClaimedAt, ResultJson) take their NULL default.
function Insert(id: Guid, now: int, expiresAfter: Option<int>): Req
{
  Req(id, Pending, now,
      if expiresAfter.Some? then Some(now + expiresAfter.value) else None,
      None, None)
}

// ExpireIfPendingAsync (cs:43-49): UPDATE SET Status='expired' WHERE Id=@Id AND Status='pending';
// returns rows affected == 1.
function ExpireIfPending(r: Req): (bool, Req)
{
  if r.Status == Pending then (true, r.(Status := Expired))
  else (false, r)
}

// The WHERE clause of ListClaimableAsync and TryClaimAsync:
// "Status" = 'pending' AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now()).
predicate ClaimableWhere(r: Req, now: int)
{
  r.Status == Pending && (r.ExpiresAt.None? || r.ExpiresAt.value > now)
}

function FilterClaimable(rows: seq<Req>, now: int): seq<Req>
  ensures forall r :: r in FilterClaimable(rows, now) ==> r in rows && ClaimableWhere(r, now)
{
  if |rows| == 0 then []
  else if ClaimableWhere(rows[0], now) then [rows[0]] + FilterClaimable(rows[1..], now)
  else FilterClaimable(rows[1..], now)
}

// ListClaimableAsync (cs:51-61): WHERE (cs:56-57), then ORDER BY/LIMIT (cs:58-59, axiom).
function ListClaimable(rows: seq<Req>, now: int, limit: int): seq<Req>
{
  OrderByRequestedAtLimit(FilterClaimable(rows, now), limit)
}

// TryClaimAsync (cs:64-71): UPDATE SET Status='running', ClaimedAt=now()
// WHERE Id=@Id AND Status='pending' AND (ExpiresAt IS NULL OR ExpiresAt > now()); == 1.
function TryClaim(r: Req, now: int): (bool, Req)
{
  if r.Status == Pending && (r.ExpiresAt.None? || r.ExpiresAt.value > now)
  then (true, r.(Status := Running, ClaimedAt := Some(now)))
  else (false, r)
}

// FailAbandonedAsync (cs:86-94), per row: UPDATE SET Status='failed', ResultJson=@ResultJson::jsonb,
// CompletedAt=now() WHERE Status='running' AND ClaimedAt < now() - @AgeSeconds * interval '1 second'.
// A NULL ClaimedAt compares NULL and selects nothing. The bool is "this row was updated".
function FailAbandoned(r: Req, now: int, age: int, resultJson: CsString): (bool, Req)
{
  if r.Status == Running && r.ClaimedAt.Some? && r.ClaimedAt.value < now - age
  then (true, r.(Status := Failed, ResultJson := Some(resultJson)))
  else (false, r)
}

// DeleteFinishedOlderThanAsync (cs:98-106), per row:
// RequestedAt < now() - age AND (Status IN ('succeeded','failed','expired')
//   OR (Status = 'pending' AND ExpiresAt < now())).
// A NULL ExpiresAt makes `ExpiresAt < now()` NULL; pending AND NULL is NULL, false OR NULL is NULL,
// and a NULL WHERE selects nothing.
function DeletesRow(r: Req, now: int, age: int): bool
{
  r.RequestedAt < now - age &&
  (r.Status in {Succeeded, Failed, Expired} ||
   (r.Status == Pending && r.ExpiresAt.Some? && r.ExpiresAt.value < now))
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "**Claiming:** `UPDATE … SET "Status"='running' WHERE "Id"=@Id AND "Status"='pending' AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())`. Zero rows means it lost the race." (spec §5.1)
lemma P1_AClaimTakesOnlyAPendingUnexpiredRequest(r: Req, now: int)
  ensures TryClaim(r, now).0 <==> (r.Status == Pending && (r.ExpiresAt.None? || r.ExpiresAt.value > now))
  ensures TryClaim(r, now).0 ==> TryClaim(r, now).1 == r.(Status := Running, ClaimedAt := Some(now))
  ensures !TryClaim(r, now).0 ==> TryClaim(r, now).1 == r
{
}

// Citation: "If the api then changes `pending` to `expired`, the worker will never run it" (spec §5.2) and "with the worker's claim holding the row, the api's `UPDATE … WHERE Status='pending'` waits, re-evaluates and affects 0 rows; the status stays `running`." (spec, Verified assumption 17)
lemma P2_AnExpiredRequestIsNeverClaimed_AndAClaimedOneNeverExpires(r: Req, now: int)
  ensures ExpireIfPending(r).0 ==> !TryClaim(ExpireIfPending(r).1, now).0
  ensures TryClaim(r, now).0 ==> !ExpireIfPending(TryClaim(r, now).1).0
{
}

// Citation: "**Polling:** every 1 s for `Status='pending' AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())`, oldest first." (spec §5.1)
lemma P3_OnlyClaimableRequestsAreListed(rows: seq<Req>, now: int, limit: int)
  ensures forall r :: r in ListClaimable(rows, now, limit) ==>
    r in rows && r.Status == Pending && (r.ExpiresAt.None? || r.ExpiresAt.value > now)
{
}

// Citation: "A row still `running` more than one hour after it was claimed is marked failed ("the worker stopped before finishing this request"), so a worker that dies mid-request never leaves a request pending forever." (spec §5.1)
lemma P4_OnlyRequestsClaimedBeforeTheCutoffAreFailed(r: Req, now: int, age: int, resultJson: CsString)
  ensures FailAbandoned(r, now, age, resultJson).0 <==>
    (r.Status == Running && r.ClaimedAt.Some? && r.ClaimedAt.value < now - age)
  ensures FailAbandoned(r, now, age, resultJson).0 ==>
    FailAbandoned(r, now, age, resultJson).1.Status == Failed &&
    FailAbandoned(r, now, age, resultJson).1.ResultJson == Some(resultJson)
{
}

// Citation: "**Cleanup:** finished rows older than one day are deleted." (spec §5.1) and "(await Requests.GetAsync(oldRunning)).Should().NotBeNull();" (plan T7 S1f, `DeleteFinishedOlderThan_RemovesOnlyOldFinishedRequests`)
lemma P5_CleanupDeletesOnlyFinishedRequests(r: Req, now: int, age: int)
  ensures DeletesRow(r, now, age) ==>
    r.RequestedAt < now - age && r.Status != Running &&
    !(r.Status == Pending && (r.ExpiresAt.None? || r.ExpiresAt.value > now))
{
}

// Citation: "**Expiry:** `ExpiresAt` is `RequestedAt + 15 s` for RegisterSchema and NULL for ReconcileType and ReplayDlq." (spec §5.1)
lemma P6_ARequestExpiresAfterItsWaitOrNever(id: Guid, now: int, expiresAfter: Option<int>)
  ensures Insert(id, now, expiresAfter).Status == Pending && Insert(id, now, expiresAfter).RequestedAt == now
  ensures Insert(id, now, expiresAfter).ExpiresAt == (if expiresAfter.Some? then Some(now + expiresAfter.value) else None)
{
}
