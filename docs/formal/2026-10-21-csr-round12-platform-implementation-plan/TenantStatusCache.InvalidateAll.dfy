// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T5 S7
// Source: Iverson.Server/Iverson.Api/Tenancy/TenantStatusCache.cs KeyFor, GetStatusAsync, Invalidate, InvalidateAll (36–52)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/TenantStatusCache.InvalidateAll.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// C# string: UTF-16 code units (--unicode-char false).
type CsString = s: seq<char> | |s| < 0x8000_0000

type ReadId = nat

// ---------------------------------------------------------------------------
// Library boundary
// ---------------------------------------------------------------------------

// long.ToString() as used by string interpolation (TenantStatusCache.cs:36). The generation is
// modelled as an int (deviation: 2^63 increments, hence wrapping, are unreachable). Digits are
// ASCII '0'..'9'; a negative value is written with '-' (never reached: the generation starts at 0
// and only increments).
function DigitChar(d: int): char
  requires 0 <= d < 10
{
  ['0','1','2','3','4','5','6','7','8','9'][d]
}

function NatToString(n: nat): seq<char>
  decreases n
{
  if n < 10 then [DigitChar(n)] else NatToString(n / 10) + [DigitChar(n % 10)]
}

function LongToString(n: int): seq<char>
{
  if n < 0 then ['-'] + NatToString(-n) else NatToString(n)
}

// IMemoryCache, as seen by this class: a map from key to the stored status. The `writer` field is
// ghost provenance (which read's cache.Set stored the entry); it never influences behaviour.
datatype Entry = Entry(status: Option<CsString>, writer: ReadId)

// State of one TenantStatusCache instance plus its IMemoryCache.
//   generation : the `_generation` field (TenantStatusCache.cs:30)
//   cache      : the IMemoryCache contents under this class's keys
//   pending    : repository reads in flight (GetStatusAsync suspended at line 44), with the key
//                built at line 40 that line 46 will Set
datatype State = State(generation: int, cache: map<CsString, Entry>, pending: map<ReadId, CsString>)

const Init: State := State(0, map[], map[])

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

const KeyPrefix: seq<char> := "tenant-status:"

// Building a string longer than the CLR allows throws (OutOfMemoryException): an outcome.
datatype KeyResult = Key(key: CsString) | TooLong

// TenantStatusCache.cs:36
//   private static string KeyFor(long generation, string tenantId) => $"tenant-status:{generation}:{tenantId}";
function KeyFor(generation: int, tenantId: CsString): KeyResult
{
  var s := KeyPrefix + LongToString(generation) + [':'] + tenantId;
  if |s| < 0x8000_0000 then Key(s) else TooLong
}

// TenantStatusCache.cs:38-43 -- GetStatusAsync up to the await.
datatype BeginOutcome =
  | Hit(status: Option<CsString>)   // line 41-42: TryGetValue true, return cachedStatus
  | Miss(key: CsString)             // line 44: goes to tenantRepository.GetAsync
  | Threw                           // line 40: KeyFor threw

function GetStatusBegin(s: State, tenantId: CsString): BeginOutcome
{
  // line 40: var key = KeyFor(Interlocked.Read(ref _generation), tenantId);
  match KeyFor(s.generation, tenantId)
  case TooLong => Threw
  case Key(key) =>
    // line 41: if (cache.TryGetValue(key, out string? cachedStatus))
    if key in s.cache then
      // line 42: return cachedStatus;
      Hit(s.cache[key].status)
    else
      Miss(key)
}

// TenantStatusCache.cs:44-47 -- GetStatusAsync after the await completes.
//   line 44-45: status = (await tenantRepository.GetAsync(tenantId))?.Status   (the `status` argument)
//   line 46:    cache.Set(key, status, Ttl);
//   line 47:    return status;
function GetStatusFinish(s: State, r: ReadId, status: Option<CsString>): State
  requires r in s.pending
{
  var key := s.pending[r];
  s.(cache := s.cache[key := Entry(status, r)], pending := s.pending - {r})
}

// TenantStatusCache.cs:50
//   public void Invalidate(string tenantId) => cache.Remove(KeyFor(Interlocked.Read(ref _generation), tenantId));
function CacheInvalidate(s: State, tenantId: CsString): State
{
  match KeyFor(s.generation, tenantId)
  case TooLong => s   // KeyFor threw; nothing removed
  case Key(key) => s.(cache := s.cache - {key})
}

// TenantStatusCache.cs:52
//   public void InvalidateAll() => Interlocked.Increment(ref _generation);
function CacheInvalidateAll(s: State): State
{
  s.(generation := s.generation + 1)
}

// ---------------------------------------------------------------------------
// Trace model
// ---------------------------------------------------------------------------

datatype Event =
  | Begin(r: ReadId, tenant: CsString)
  | Finish(r: ReadId, status: Option<CsString>)
  | InvalidateAll
  | Invalidate(tenant: CsString)
  | Evict(key: CsString)

function Step(s: State, e: Event): State
{
  match e
  case Begin(r, tenant) =>
    (match GetStatusBegin(s, tenant)
     case Miss(key) => s.(pending := s.pending[r := key])
     case Hit(_) => s
     case Threw => s)
  case Finish(r, status) =>
    if r in s.pending then GetStatusFinish(s, r, status) else s
  case InvalidateAll => CacheInvalidateAll(s)
  case Invalidate(tenant) => CacheInvalidate(s, tenant)
  case Evict(key) => s.(cache := s.cache - {key})   // TTL expiry / memory pressure
}

function Run(p: seq<Event>): State
  decreases |p|
{
  if |p| == 0 then Init else Step(Run(p[..|p| - 1]), p[|p| - 1])
}

function GenAt(t: seq<Event>, n: nat): int
  requires n <= |t|
{
  Run(t[..n]).generation
}

predicate UniqueBegins(t: seq<Event>)
{
  forall a, b :: 0 <= a < b < |t| && t[a].Begin? && t[b].Begin? ==> t[a].r != t[b].r
}

// Every Finish(r, _) completes a Begin(r, _) that missed and has not finished yet.
predicate FinishesFollowMisses(t: seq<Event>)
{
  forall m :: 0 <= m < |t| && t[m].Finish? ==> t[m].r in Run(t[..m]).pending
}

predicate ValidTrace(t: seq<Event>)
{
  UniqueBegins(t) && FinishesFollowMisses(t)
}

// For a Begin at j that hit: the read whose Finish stored the entry it was served from.
function ServedFrom(t: seq<Event>, j: nat): Option<ReadId>
{
  if j < |t| && t[j].Begin? then
    var s := Run(t[..j]);
    match KeyFor(s.generation, t[j].tenant)
    case TooLong => None
    case Key(key) => if key in s.cache then Some(s.cache[key].writer) else None
  else None
}

function FindBegin(t: seq<Event>, r: ReadId, from: nat): nat
  decreases |t| - from
{
  if from >= |t| then |t|
  else if t[from].Begin? && t[from].r == r then from
  else FindBegin(t, r, from + 1)
}

function BeganAt(t: seq<Event>, r: ReadId): nat
{
  FindBegin(t, r, 0)
}

function ReadTenant(t: seq<Event>, r: ReadId): CsString
{
  var b := BeganAt(t, r);
  if b < |t| && t[b].Begin? then t[b].tenant else []
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma StepRun(t: seq<Event>, n: nat)
  requires n < |t|
  ensures Run(t[..n + 1]) == Step(Run(t[..n]), t[n])
{
  assert t[..n + 1][..n] == t[..n];
  assert t[..n + 1][n] == t[n];
}

lemma GenStep(t: seq<Event>, n: nat)
  requires n < |t|
  ensures GenAt(t, n) <= GenAt(t, n + 1)
  ensures t[n].InvalidateAll? ==> GenAt(t, n + 1) == GenAt(t, n) + 1
{
  StepRun(t, n);
}

lemma GenMono(t: seq<Event>, a: nat, b: nat)
  requires a <= b <= |t|
  ensures GenAt(t, a) <= GenAt(t, b)
  decreases b - a
{
  if a < b {
    GenMono(t, a, b - 1);
    GenStep(t, b - 1);
  }
}

lemma NatToStringShape(n: nat)
  ensures |NatToString(n)| >= 1
  ensures forall c :: c in NatToString(n) ==> '0' <= c <= '9'
  ensures n < 10 ==> |NatToString(n)| == 1
  ensures n >= 10 ==> |NatToString(n)| >= 2
  decreases n
{
  if n >= 10 {
    NatToStringShape(n / 10);
  }
}

lemma DigitCharInjective(a: int, b: int)
  requires 0 <= a < 10 && 0 <= b < 10 && DigitChar(a) == DigitChar(b)
  ensures a == b
{
}

lemma NatToStringInjective(a: nat, b: nat)
  requires NatToString(a) == NatToString(b)
  ensures a == b
  decreases a
{
  NatToStringShape(a);
  NatToStringShape(b);
  if a < 10 && b < 10 {
    DigitCharInjective(a, b);
  } else if a >= 10 && b >= 10 {
    var sa := NatToString(a);
    var sb := NatToString(b);
    assert sa == NatToString(a / 10) + [DigitChar(a % 10)];
    assert sb == NatToString(b / 10) + [DigitChar(b % 10)];
    assert sa[|sa| - 1] == DigitChar(a % 10);
    assert sb[|sb| - 1] == DigitChar(b % 10);
    DigitCharInjective(a % 10, b % 10);
    assert sa[..|sa| - 1] == NatToString(a / 10);
    assert sb[..|sb| - 1] == NatToString(b / 10);
    NatToStringInjective(a / 10, b / 10);
  }
}

lemma LongToStringShape(n: int)
  ensures forall c :: c in LongToString(n) ==> c != ':'
{
  if n < 0 { NatToStringShape(-n); } else { NatToStringShape(n); }
}

lemma LongToStringFirst(n: int)
  ensures |LongToString(n)| >= 1
  ensures (LongToString(n)[0] == '-') == (n < 0)
{
  if n < 0 { NatToStringShape(-n); } else {
    NatToStringShape(n);
    assert NatToString(n)[0] in NatToString(n);
  }
}

lemma LongToStringInjective(a: int, b: int)
  requires LongToString(a) == LongToString(b)
  ensures a == b
{
  LongToStringFirst(a);
  LongToStringFirst(b);
  assert (a < 0) == (b < 0);
  if a < 0 {
    assert LongToString(a)[1..] == NatToString(-a);
    assert LongToString(b)[1..] == NatToString(-b);
    NatToStringInjective(-a, -b);
  } else {
    NatToStringInjective(a, b);
  }
}

// In a + [':'] + x with no ':' in a, the first ':' is at |a|.
lemma FirstColonAt(a: seq<char>, x: seq<char>)
  requires forall c :: c in a ==> c != ':'
  ensures (a + [':'] + x)[|a|] == ':'
  ensures forall k :: 0 <= k < |a| ==> (a + [':'] + x)[k] != ':'
{
  forall k | 0 <= k < |a|
    ensures (a + [':'] + x)[k] != ':'
  {
    assert (a + [':'] + x)[k] == a[k];
    assert a[k] in a;
  }
}

lemma SplitAtColon(a: seq<char>, x: seq<char>, b: seq<char>, y: seq<char>)
  requires forall c :: c in a ==> c != ':'
  requires forall c :: c in b ==> c != ':'
  requires a + [':'] + x == b + [':'] + y
  ensures a == b && x == y
{
  var s := a + [':'] + x;
  FirstColonAt(a, x);
  FirstColonAt(b, y);
  assert !(|a| < |b|);
  assert !(|b| < |a|);
  assert |a| == |b|;
  assert a == s[..|a|] == b;
  assert x == s[|a| + 1..] == y;
}

lemma KeyForInjective(g1: int, t1: CsString, g2: int, t2: CsString, k: CsString)
  requires KeyFor(g1, t1) == Key(k) && KeyFor(g2, t2) == Key(k)
  ensures g1 == g2 && t1 == t2
{
  var s1 := KeyPrefix + LongToString(g1) + [':'] + t1;
  var s2 := KeyPrefix + LongToString(g2) + [':'] + t2;
  assert s1 == k == s2;
  assert s1[|KeyPrefix|..] == LongToString(g1) + [':'] + t1;
  assert s2[|KeyPrefix|..] == LongToString(g2) + [':'] + t2;
  LongToStringShape(g1);
  LongToStringShape(g2);
  SplitAtColon(LongToString(g1), t1, LongToString(g2), t2);
  LongToStringInjective(g1, g2);
}

lemma FindBeginIs(t: seq<Event>, r: ReadId, from: nat, b: nat)
  requires UniqueBegins(t)
  requires from <= b < |t| && t[b].Begin? && t[b].r == r
  ensures FindBegin(t, r, from) == b
  decreases b - from
{
  if from < b {
    assert t[from].Begin? ==> t[from].r != t[b].r;
    FindBeginIs(t, r, from + 1, b);
  }
}

// ----- Provenance invariant -----

ghost predicate PendingWitness(t: seq<Event>, n: nat, r: ReadId, key: CsString)
  requires n <= |t|
{
  exists b :: 0 <= b < n && t[b].Begin? && t[b].r == r && KeyFor(GenAt(t, b), t[b].tenant) == Key(key)
}

ghost predicate CacheWitness(t: seq<Event>, n: nat, key: CsString, w: ReadId)
  requires n <= |t|
{
  exists b, m :: 0 <= b < m < n && t[b].Begin? && t[b].r == w && t[m].Finish? && t[m].r == w
    && KeyFor(GenAt(t, b), t[b].tenant) == Key(key)
}

ghost predicate Inv(t: seq<Event>, n: nat)
  requires n <= |t|
{
  var s := Run(t[..n]);
  (forall key :: key in s.cache ==> CacheWitness(t, n, key, s.cache[key].writer))
  && (forall r :: r in s.pending ==> PendingWitness(t, n, r, s.pending[r]))
}

lemma InvStep(t: seq<Event>, n: nat)
  requires n < |t| && Inv(t, n)
  ensures Inv(t, n + 1)
{
  StepRun(t, n);
  var s := Run(t[..n]);
  var s' := Run(t[..n + 1]);
  assert s' == Step(s, t[n]);
  forall key | key in s'.cache
    ensures CacheWitness(t, n + 1, key, s'.cache[key].writer)
  {
    if t[n].Finish? && t[n].r in s.pending && key == s.pending[t[n].r] {
      var r := t[n].r;
      assert s'.cache[key] == Entry(t[n].status, r);
      assert PendingWitness(t, n, r, s.pending[r]);
      var b :| 0 <= b < n && t[b].Begin? && t[b].r == r && KeyFor(GenAt(t, b), t[b].tenant) == Key(key);
      assert 0 <= b < n < n + 1 && t[b].Begin? && t[b].r == r && t[n].Finish? && t[n].r == r
        && KeyFor(GenAt(t, b), t[b].tenant) == Key(key);
    } else {
      assert key in s.cache && s'.cache[key] == s.cache[key];
      var w := s.cache[key].writer;
      assert CacheWitness(t, n, key, w);
      var b, m :| 0 <= b < m < n && t[b].Begin? && t[b].r == w && t[m].Finish? && t[m].r == w
        && KeyFor(GenAt(t, b), t[b].tenant) == Key(key);
      assert 0 <= b < m < n + 1;
    }
  }
  forall r | r in s'.pending
    ensures PendingWitness(t, n + 1, r, s'.pending[r])
  {
    if t[n].Begin? && r == t[n].r && GetStatusBegin(s, t[n].tenant).Miss? {
      var key := GetStatusBegin(s, t[n].tenant).key;
      assert s'.pending[r] == key;
      assert KeyFor(GenAt(t, n), t[n].tenant) == Key(key);
      assert 0 <= n < n + 1 && t[n].Begin? && t[n].r == r;
    } else {
      assert r in s.pending && s'.pending[r] == s.pending[r];
      assert PendingWitness(t, n, r, s.pending[r]);
      var b :| 0 <= b < n && t[b].Begin? && t[b].r == r && KeyFor(GenAt(t, b), t[b].tenant) == Key(s.pending[r]);
      assert 0 <= b < n + 1;
    }
  }
}

lemma InvAll(t: seq<Event>, n: nat)
  requires n <= |t|
  ensures Inv(t, n)
  decreases n
{
  if n == 0 {
    assert t[..0] == [];
  } else {
    InvAll(t, n - 1);
    InvStep(t, n - 1);
  }
}

// If Begin at j is served from read w's entry, w began at some b < j with the same generation and
// tenant as the read at j, and finished at some m with b < m < j.
lemma ServedProvenance(t: seq<Event>, j: nat, w: ReadId) returns (b: nat, m: nat)
  requires j < |t| && t[j].Begin? && ServedFrom(t, j) == Some(w)
  ensures 0 <= b < m < j && t[b].Begin? && t[b].r == w && t[m].Finish? && t[m].r == w
  ensures GenAt(t, b) == GenAt(t, j) && t[b].tenant == t[j].tenant
{
  InvAll(t, j);
  var s := Run(t[..j]);
  var key := KeyFor(s.generation, t[j].tenant).key;
  assert key in s.cache && s.cache[key].writer == w;
  assert CacheWitness(t, j, key, w);
  b, m :| 0 <= b < m < j && t[b].Begin? && t[b].r == w && t[m].Finish? && t[m].r == w
    && KeyFor(GenAt(t, b), t[b].tenant) == Key(key);
  KeyForInjective(GenAt(t, b), t[b].tenant, GenAt(t, j), t[j].tenant, key);
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: test `InvalidateAll_DuringARepositoryRead_TheReadsResultIsNotServedAfterwards` (T5 S5), generalised; "On every notification it invalidates both `TokenRevocationCache` and `TenantStatusCache`." (§2.3 line 85)
lemma P1_ReadBeforeInvalidateAllNeverServedAfter(t: seq<Event>, i: nat, j: nat, k: ReadId)
  requires ValidTrace(t) && i < j < |t| && t[i].InvalidateAll? && t[j].Begin?
  requires ServedFrom(t, j) == Some(k)
  ensures BeganAt(t, k) > i
{
  var b, m := ServedProvenance(t, j, k);
  FindBeginIs(t, k, 0, b);
  if b <= i {
    GenMono(t, b, i);
    GenStep(t, i);
    GenMono(t, i + 1, j);
    assert false;
  }
}

// Citation: test `InvalidateAll_EveryTenantReadsTheRepositoryAgain` (T5 S5), generalised
lemma P2_FirstReadAfterInvalidateAllGoesToTheRepository(t: seq<Event>, i: nat, j: nat)
  requires ValidTrace(t) && i < j < |t| && t[i].InvalidateAll? && t[j].Begin?
  requires forall m :: i < m < j && t[m].Finish? ==> !(BeganAt(t, t[m].r) > i && ReadTenant(t, t[m].r) == t[j].tenant)
  ensures ServedFrom(t, j).None?
{
  if ServedFrom(t, j).Some? {
    var w := ServedFrom(t, j).value;
    var b, m := ServedProvenance(t, j, w);
    FindBeginIs(t, w, 0, b);
    if b <= i {
      GenMono(t, b, i);
      GenStep(t, i);
      GenMono(t, i + 1, j);
      assert false;
    }
    assert false;
  }
}
