// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T2 S4
// Source: Iverson.Server/Iverson.Api/Grpc/RateLimitInterceptor.cs AdminServices, _limiter, _adminLimiter (13–37), Enforce (58–74)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/RateLimitInterceptor.Enforce.dfy

// ---- Shared vocabulary ----
datatype Option<T> = None | Some(value: T)
type CsString = s: seq<char> | |s| < 0x8000_0000

// ---- Approved axiom (library boundary: System.Threading.RateLimiting sliding window, one window) ----
function SlidingWindowGrants<K(==)>(granted: seq<K>, key: K, permitLimit: nat): bool
  ensures SlidingWindowGrants(granted, key, permitLimit) <==> multiset(granted)[key] < permitLimit

// ---- Model ----
// sub: the "sub" claim's Value (None when FindFirst returns null).
// routed: GrpcMethodMetadata.Method.ServiceName of the endpoint routing matched (None when any `?.` is null).
datatype Call = Call(sub: Option<CsString>, routed: Option<CsString>)

// Partition keys granted a permit in the current one-minute window, per limiter.
datatype Windows = Windows(general: seq<CsString>, admin: seq<(CsString, CsString)>)

datatype EnforceResult = EnforceResult(acquired: bool, next: Windows)

const TenantAdminService: CsString := "iverson.TenantAdminGrpcService"
const TenantLifecycleService: CsString := "iverson.TenantLifecycleGrpcService"
const UnknownSubject: CsString := "unknown"

function GeneralCount(w: Windows, sub: CsString): nat
{
  multiset(w.general)[sub]
}

function AdminCount(w: Windows, service: CsString, sub: CsString): nat
{
  multiset(w.admin)[(service, sub)]
}

// ---- Transliteration ----

// RateLimitInterceptor.cs:13–17 AdminServices
const AdminServices: seq<CsString> := [TenantAdminService, TenantLifecycleService]

// RateLimitInterceptor.cs:23, 33 PermitLimit
const GeneralPermitLimit: nat := 50000
const AdminPermitLimit: nat := 30

// Enumerable.FirstOrDefault(services, service => service == matched): first element equal to
// `matched`, else null. Ordinal string equality; null `matched` never equals a non-null element.
function FirstOrDefault(services: seq<CsString>, matched: Option<CsString>): Option<CsString>
  decreases |services|
{
  if |services| == 0 then None
  else if matched == Some(services[0]) then Some(services[0])
  else FirstOrDefault(services[1..], matched)
}

// _limiter.AttemptAcquire(subject): RateLimitInterceptor.cs:19–27, 67
function AcquireGeneral(w: Windows, subject: CsString): EnforceResult
{
  if SlidingWindowGrants(w.general, subject, GeneralPermitLimit)
  then EnforceResult(true, w.(general := w.general + [subject]))
  else EnforceResult(false, w)
}

// _adminLimiter.AttemptAcquire((adminService, subject)): RateLimitInterceptor.cs:29–37, 68
function AcquireAdmin(w: Windows, key: (CsString, CsString)): EnforceResult
{
  if SlidingWindowGrants(w.admin, key, AdminPermitLimit)
  then EnforceResult(true, w.(admin := w.admin + [key]))
  else EnforceResult(false, w)
}

// Enforce: RateLimitInterceptor.cs:58–74. `acquired == false` is the RpcException(ResourceExhausted) outcome.
function Enforce(w: Windows, c: Call): EnforceResult
{
  // line 60
  var subject: CsString := if c.sub.Some? then c.sub.value else UnknownSubject;
  // lines 63–64
  var matched := c.routed;
  // line 65
  var adminService := FirstOrDefault(AdminServices, matched);
  // lines 66–68
  var lease := if adminService.None?
               then AcquireGeneral(w, subject)
               else AcquireAdmin(w, (adminService.value, subject));
  // lines 69–73 (LogWarning is a side-effect-free logging call here; the throw is acquired == false)
  if !lease.acquired then EnforceResult(false, lease.next)
  else EnforceResult(true, lease.next)
}

// ---- Helper lemmas ----

lemma ServiceNamesDistinct()
  ensures TenantAdminService != TenantLifecycleService
{
  assert TenantAdminService[8] == 'T';
  assert TenantAdminService[14] == 'A';
  assert TenantLifecycleService[14] == 'L';
}

lemma FirstOrDefaultAdmin()
  ensures FirstOrDefault(AdminServices, Some(TenantAdminService)) == Some(TenantAdminService)
{
}

lemma FirstOrDefaultLifecycle()
  ensures FirstOrDefault(AdminServices, Some(TenantLifecycleService)) == Some(TenantLifecycleService)
{
  ServiceNamesDistinct();
  assert AdminServices[1..] == [TenantLifecycleService];
}

lemma FirstOrDefaultOther(m: Option<CsString>)
  requires m != Some(TenantAdminService) && m != Some(TenantLifecycleService)
  ensures FirstOrDefault(AdminServices, m) == None
{
  var rest := AdminServices[1..];
  assert rest == [TenantLifecycleService];
  assert rest[1..] == [];
  assert FirstOrDefault(rest[1..], m) == None;
  assert FirstOrDefault(rest, m) == FirstOrDefault(rest[1..], m);
  assert FirstOrDefault(AdminServices, m) == FirstOrDefault(rest, m);
}

// ---- Properties ----

// Citation: design RateLimitPartition.P1_ThirtyFirstRefusedOthersPass: "the 31st TenantAdmin call in a minute is refused while other RPCs pass" (§6 line 490)
lemma P1_ThirtyFirstRefusedOthersPass(w: Windows, sub: CsString, other: CsString)
  requires other != TenantAdminService && other != TenantLifecycleService
  requires AdminCount(w, TenantAdminService, sub) == 30 && GeneralCount(w, sub) < 50000
  ensures !Enforce(w, Call(Some(sub), Some(TenantAdminService))).acquired
       && Enforce(w, Call(Some(sub), Some(other))).acquired
{
  FirstOrDefaultAdmin();
  FirstOrDefaultOther(Some(other));
}

// Citation: design RateLimitPartition.P2_SeparateBuckets: "partitioned by `(service, sub)`, so each of the two services has its own 30/min bucket per caller" (§2.1 line 45)
lemma P2_SeparateBuckets(w: Windows, sub: CsString)
  requires AdminCount(w, TenantAdminService, sub) == 30 && AdminCount(w, TenantLifecycleService, sub) < 30
  ensures Enforce(w, Call(Some(sub), Some(TenantLifecycleService))).acquired
{
  FirstOrDefaultLifecycle();
}

// Citation: test `One_callers_spent_TenantAdmin_budget_leaves_another_callers_untouched` (T2 S1 1a), generalised
lemma P3_AnotherCallersBucketUntouched(w: Windows, a: CsString, b: CsString)
  requires a != b && AdminCount(w, TenantAdminService, b) < 30
  ensures Enforce(Enforce(w, Call(Some(a), Some(TenantAdminService))).next, Call(Some(b), Some(TenantAdminService))).acquired
{
  FirstOrDefaultAdmin();
  var w1 := Enforce(w, Call(Some(a), Some(TenantAdminService))).next;
  assert w1.admin == w.admin || w1.admin == w.admin + [(TenantAdminService, a)];
  assert multiset(w.admin + [(TenantAdminService, a)]) == multiset(w.admin) + multiset{(TenantAdminService, a)};
  assert (TenantAdminService, a) != (TenantAdminService, b);
  assert AdminCount(w1, TenantAdminService, b) == AdminCount(w, TenantAdminService, b);
}

// Citation: "`RateLimitInterceptor` keeps its 50,000/min per-`sub` limiter for every RPC except the `TenantAdminGrpcService` and `TenantLifecycleGrpcService` methods. Those go to a second limiter" (§2.1 line 45)
lemma P4_OtherRpcsNeverSpendTheAdminBucket(w: Windows, c: Call)
  requires c.routed != Some(TenantAdminService) && c.routed != Some(TenantLifecycleService)
  ensures Enforce(w, c).next.admin == w.admin
{
  FirstOrDefaultOther(c.routed);
}
