// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a782ac7674fde9185ad55bb1a986df3008adf050
// Source: §2.1 "Limits" (and §6 line 490), lines 44-47
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/RateLimitPartition.dfy
// Round: 2

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// §2.1 line 45: the RPCs split into "the `TenantAdminGrpcService` and
// `TenantLifecycleGrpcService` methods" and "every RPC except" those.
datatype Service = TenantAdmin | TenantLifecycle | Other

// §2.1 line 45: the limiters are keyed by the caller's `sub` (and, for the
// admin limiter, the service). A call is identified by those two values only.
datatype Call = Call(sub: string, service: Service)

// §2.1 line 45: "the 50,000/min per-`sub` limiter".
const GeneralLimit: int := 50000
// §2.1 line 45: "a second limiter at **30 calls per minute**".
const AdminLimit: int := 30

// §2.1 line 45: "except the `TenantAdminGrpcService` and
// `TenantLifecycleGrpcService` methods. Those go to a second limiter".
predicate IsAdminService(s: Service)
{
  s == TenantAdmin || s == TenantLifecycle
}

// §2.1 line 45: "partitioned by `(service, sub)`": the admin bucket of a
// (sub, service) pair counts the admitted calls in the window with that
// exact pair.
function CountFor(calls: seq<Call>, sub: string, s: Service): nat
{
  if |calls| == 0 then 0
  else CountFor(calls[..|calls|-1], sub, s)
       + (if calls[|calls|-1].sub == sub && calls[|calls|-1].service == s then 1 else 0)
}

// §2.1 line 45: "keeps its 50,000/min per-`sub` limiter for every RPC except
// the TenantAdmin ... and TenantLifecycle ... methods. Those go to a second
// limiter": admin-service calls are routed away from the general limiter, so
// the general bucket of `sub` counts only that sub's non-admin calls.
function GeneralCount(calls: seq<Call>, sub: string): nat
{
  if |calls| == 0 then 0
  else GeneralCount(calls[..|calls|-1], sub)
       + (if calls[|calls|-1].sub == sub && !IsAdminService(calls[|calls|-1].service) then 1 else 0)
}

// §2.1 line 45, cases in the spec's order: first the general limiter for
// every RPC except the two admin services, then the second limiter for those.
// "Over the limit is `ResourceExhausted`": a call is refused exactly when its
// bucket already holds its limit of admitted calls in the current minute.
predicate Allowed(calls: seq<Call>, c: Call)
{
  if !IsAdminService(c.service) then
    GeneralCount(calls, c.sub) < GeneralLimit
  else
    CountFor(calls, c.sub, c.service) < AdminLimit
}

// "in a minute" (§6 line 490) / "per minute" (§2.1 line 45): `calls` is the
// log of calls admitted in the current one-minute window, in arrival order;
// each was admitted by the rule given the calls admitted before it.
predicate ValidWindow(calls: seq<Call>)
{
  forall i :: 0 <= i < |calls| ==> Allowed(calls[..i], calls[i])
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "the 31st TenantAdmin call in a minute is refused while other RPCs pass" (§6 line 490)
lemma P1_ThirtyFirstRefusedOthersPass(calls: seq<Call>, sub: string)
  requires ValidWindow(calls) && CountFor(calls, sub, TenantAdmin) == 30 && GeneralCount(calls, sub) < 50000
  ensures !Allowed(calls, Call(sub, TenantAdmin)) && Allowed(calls, Call(sub, Other))
{
}

// Citation: "partitioned by `(service, sub)`, so each of the two services has its own 30/min bucket per caller" (§2.1 line 45)
lemma P2_SeparateBuckets(calls: seq<Call>, sub: string)
  requires ValidWindow(calls) && CountFor(calls, sub, TenantAdmin) == 30 && CountFor(calls, sub, TenantLifecycle) < 30 && GeneralCount(calls, sub) < 50000
  ensures Allowed(calls, Call(sub, TenantLifecycle))
{
}
