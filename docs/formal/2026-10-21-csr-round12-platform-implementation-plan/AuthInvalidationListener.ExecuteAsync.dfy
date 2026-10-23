// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T5 S15
// Source: Iverson.Server/Iverson.Api/Tenancy/AuthInvalidationListener.cs ExecuteAsync, InvalidateCaches (43–97)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-21-csr-round12-platform-implementation-plan/AuthInvalidationListener.ExecuteAsync.dfy

// ---------------------------------------------------------------------------------------------
// Environment: the script of connection attempts (library boundary; an input, not an axiom).
//   OpenOrListenFails  : OpenAsync or the LISTEN command threw something other than a cancellation
//                        OperationCanceledException (caught at lines 68–75).
//   Listens(n, lost)   : LISTEN succeeded; n notifications arrived during WaitAsync; then either the
//                        connection was lost (lost: WaitAsync threw, caught at 68–75) or the stopping
//                        token cancelled WaitAsync (!lost: caught at 64–66, return).
//   Stopped            : cancellation during OpenAsync, LISTEN or the preceding Task.Delay (or seen by
//                        the loop head at line 47): the method returns.
// ---------------------------------------------------------------------------------------------
datatype Attempt = OpenOrListenFails | Listens(notifications: nat, lost: bool) | Stopped

// Listen: the LISTEN command succeeded (54–55). InvalidateBoth: one InvalidateCaches() call (93–97).
// Delay(ticks): Task.Delay(retryDelay) (79). Return: the method returns (66, 83, or the loop head 47).
datatype Action = Listen | InvalidateBoth | Delay(ticks: int) | Return

// C# long.
newtype Int64 = x: int | -0x8000_0000_0000_0000 <= x < 0x8000_0000_0000_0000

// Unchecked long arithmetic wraps modulo 2^64.
function Wrap64(x: int): Int64
{
  ((x + 0x8000_0000_0000_0000) % 0x1_0000_0000_0000_0000 - 0x8000_0000_0000_0000) as Int64
}

function WrapMul64(a: Int64, b: Int64): Int64
{
  Wrap64(a as int * b as int)
}

// Math.Min(long, long).
function Int64Min(a: Int64, b: Int64): Int64
{
  if a <= b then a else b
}

const OneSecondTicks := 10_000_000
const ThirtySecondTicks := 300_000_000

// TimeSpan values are modelled by their Ticks (long). Lines 40–41.
const FirstRetryDelay: Int64 := 10_000_000   // TimeSpan.FromSeconds(1).Ticks
const MaxRetryDelay: Int64 := 300_000_000    // TimeSpan.FromSeconds(30).Ticks

function RepeatAction(a: Action, n: nat): seq<Action>
{
  if n == 0 then [] else [a] + RepeatAction(a, n - 1)
}

function Min(a: int, b: int): int
{
  if a <= b then a else b
}

function Pow2(n: nat): nat
{
  if n == 0 then 1 else 2 * Pow2(n - 1)
}

// ---------------------------------------------------------------------------------------------
// Transliteration of ExecuteAsync (43–87) and InvalidateCaches (93–97).
// ---------------------------------------------------------------------------------------------

// The try block at lines 49–75 for one attempt: the actions it performs, whether it returned
// (line 66), and the value of retryDelay when it is left.
datatype TryOutcome = TryOutcome(actions: seq<Action>, returned: bool, retryDelay: Int64)

function TryBlock(a: Attempt, retryDelay: Int64): TryOutcome
{
  match a
  // 53/55 cancelled -> catch (OperationCanceledException) when cancelled (64–66): return
  case Stopped => TryOutcome([], true, retryDelay)
  // 53/55 threw -> catch (Exception) (68–75): count and log (ignored), fall through to the delay
  case OpenOrListenFails => TryOutcome([], false, retryDelay)
  case Listens(n, lost) =>
    // 54–55 LISTEN succeeded; 58 InvalidateCaches()
    var afterListen := [Listen, InvalidateBoth];
    // 59 retryDelay = FirstRetryDelay
    var rd := FirstRetryDelay;
    // 61–62 while (true) WaitAsync: each notification runs the handler wired at 52 -> InvalidateCaches()
    var afterWait := afterListen + RepeatAction(InvalidateBoth, n);
    if lost then
      // WaitAsync threw -> catch (Exception) (68–75)
      TryOutcome(afterWait, false, rd)
    else
      // WaitAsync cancelled -> catch (OperationCanceledException) when cancelled (64–66): return
      TryOutcome(afterWait, true, rd)
}

// retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks)) (85)
function NextRetryDelay(retryDelay: Int64): Int64
{
  Int64Min(WrapMul64(retryDelay, 2), MaxRetryDelay)
}

// The while loop at 47–86, from attempt i with the current retryDelay. The script being exhausted
// ends the observed trace.
function Loop(script: seq<Attempt>, i: nat, retryDelay: Int64): seq<Action>
  decreases |script| - i
{
  if i >= |script| then []
  else
    var t := TryBlock(script[i], retryDelay);
    if t.returned then t.actions + [Return]
    else
      // 77–80 await Task.Delay(retryDelay, stoppingToken); a cancellation there is the next
      // attempt's Stopped (81–84: return)
      t.actions + [Delay(t.retryDelay as int)] + Loop(script, i + 1, NextRetryDelay(t.retryDelay))
}

// ExecuteAsync (43–46): retryDelay starts at FirstRetryDelay.
function Run(script: seq<Attempt>): seq<Action>
{
  Loop(script, 0, FirstRetryDelay)
}

// ---------------------------------------------------------------------------------------------
// Observations over Run(script).
// ---------------------------------------------------------------------------------------------

predicate NoDelay(s: seq<Action>)
{
  forall i :: 0 <= i < |s| ==> !s[i].Delay?
}

// Split a trace at its Delay actions: segment k is what attempt k did before its delay.
function Split(acts: seq<Action>): seq<seq<Action>>
  ensures |Split(acts)| >= 1
  decreases |acts|
{
  if |acts| == 0 then [[]]
  else if acts[0].Delay? then [[]] + Split(acts[1..])
  else
    var s := Split(acts[1..]);
    [[acts[0]] + s[0]] + s[1..]
}

// The ticks of every Delay action, in order.
function Delays(acts: seq<Action>): seq<int>
  decreases |acts|
{
  if |acts| == 0 then []
  else if acts[0].Delay? then [acts[0].ticks] + Delays(acts[1..])
  else Delays(acts[1..])
}

function StripReturn(s: seq<Action>): seq<Action>
{
  if |s| > 0 && s[|s| - 1] == Return then s[..|s| - 1] else s
}

// The actions attempt k produces before its delay, excluding a final Return.
function SessionActions(script: seq<Attempt>, k: nat): seq<Action>
{
  var segs := Split(Run(script));
  if k < |segs| then StripReturn(segs[k]) else []
}

// The ticks of the Delay that follows attempt k (-1 when there is none).
function DelayAfter(script: seq<Attempt>, k: nat): int
{
  var ds := Delays(Run(script));
  if k < |ds| then ds[k] else -1
}

// The attempt is followed by a delay and another attempt.
predicate Continues(a: Attempt)
{
  !a.Stopped? && (a.Listens? ==> a.lost)
}

// k - L for the last L <= k with script[L].Listens?, else k.
function RunLength(script: seq<Attempt>, k: nat): nat
  requires k < |script|
{
  if script[k].Listens? then 0
  else if k == 0 then 0
  else RunLength(script, k - 1) + 1
}

// ---------------------------------------------------------------------------------------------
// Proof helpers.
// ---------------------------------------------------------------------------------------------

// The value of retryDelay at the loop head before attempt k, when every earlier attempt continued.
function RdIn(script: seq<Attempt>, k: nat): Int64
  requires k <= |script|
{
  if k == 0 then FirstRetryDelay
  else NextRetryDelay(TryBlock(script[k - 1], RdIn(script, k - 1)).retryDelay)
}

lemma SplitNoDelayPrefix(a: seq<Action>, b: seq<Action>)
  requires NoDelay(a)
  ensures Split(a + b) == [a + Split(b)[0]] + Split(b)[1..]
  decreases |a|
{
  if |a| == 0 {
    assert a + b == b;
    var s := Split(b);
    assert s == [s[0]] + s[1..];
    assert a + s[0] == s[0];
  } else {
    var ab := a + b;
    assert ab[0] == a[0];
    assert ab[1..] == a[1..] + b;
    SplitNoDelayPrefix(a[1..], b);
    assert [a[0]] + (a[1..] + Split(b)[0]) == a + Split(b)[0];
  }
}

lemma DelaysNoDelayPrefix(a: seq<Action>, b: seq<Action>)
  requires NoDelay(a)
  ensures Delays(a + b) == Delays(b)
  decreases |a|
{
  if |a| == 0 {
    assert a + b == b;
  } else {
    var ab := a + b;
    assert ab[0] == a[0];
    assert ab[1..] == a[1..] + b;
    DelaysNoDelayPrefix(a[1..], b);
  }
}

lemma RepeatNoDelay(a: Action, n: nat)
  requires !a.Delay?
  ensures NoDelay(RepeatAction(a, n))
  ensures |RepeatAction(a, n)| == n
  ensures forall i :: 0 <= i < n ==> RepeatAction(a, n)[i] == a
{
  if n > 0 {
    RepeatNoDelay(a, n - 1);
  }
}

lemma TryBlockNoDelay(a: Attempt, rd: Int64)
  ensures NoDelay(TryBlock(a, rd).actions)
  ensures a.Listens? ==> TryBlock(a, rd).actions == [Listen, InvalidateBoth] + RepeatAction(InvalidateBoth, a.notifications)
  ensures a.Listens? ==> TryBlock(a, rd).retryDelay == FirstRetryDelay
  ensures !a.Listens? ==> TryBlock(a, rd).retryDelay == rd
  ensures TryBlock(a, rd).returned <==> !Continues(a)
{
  if a.Listens? {
    RepeatNoDelay(InvalidateBoth, a.notifications);
  }
}

// One continuing attempt peels off one segment and one delay.
lemma LoopStep(script: seq<Attempt>, i: nat, rd: Int64)
  requires i < |script| && Continues(script[i])
  ensures var t := TryBlock(script[i], rd);
    Split(Loop(script, i, rd)) == [t.actions] + Split(Loop(script, i + 1, NextRetryDelay(t.retryDelay)))
  ensures var t := TryBlock(script[i], rd);
    Delays(Loop(script, i, rd)) == [t.retryDelay as int] + Delays(Loop(script, i + 1, NextRetryDelay(t.retryDelay)))
{
  var t := TryBlock(script[i], rd);
  TryBlockNoDelay(script[i], rd);
  var d := Delay(t.retryDelay as int);
  var rest := Loop(script, i + 1, NextRetryDelay(t.retryDelay));
  assert Loop(script, i, rd) == t.actions + ([d] + rest);
  SplitNoDelayPrefix(t.actions, [d] + rest);
  assert ([d] + rest)[1..] == rest;
  assert Split([d] + rest) == [[]] + Split(rest);
  assert t.actions + [] == t.actions;
  DelaysNoDelayPrefix(t.actions, [d] + rest);
}

// Skipping the continuing attempts i..k-1 leaves attempt k's segment at the head.
lemma Shift(script: seq<Attempt>, i: nat, k: nat)
  requires i <= k < |script| && forall j :: i <= j < k ==> Continues(script[j])
  ensures |Split(Loop(script, i, RdIn(script, i)))| >= k - i + 1
  ensures Split(Loop(script, i, RdIn(script, i)))[k - i..] == Split(Loop(script, k, RdIn(script, k)))
  ensures |Delays(Loop(script, i, RdIn(script, i)))| >= k - i
  ensures Delays(Loop(script, i, RdIn(script, i)))[k - i..] == Delays(Loop(script, k, RdIn(script, k)))
  decreases k - i
{
  if i < k {
    LoopStep(script, i, RdIn(script, i));
    assert RdIn(script, i + 1) == NextRetryDelay(TryBlock(script[i], RdIn(script, i)).retryDelay);
    Shift(script, i + 1, k);
  }
}

lemma Prefix(script: seq<Attempt>, k: nat)
  requires k < |script| && forall j :: 0 <= j < k ==> Continues(script[j])
  ensures |Split(Run(script))| > k
  ensures Split(Run(script))[k] == Split(Loop(script, k, RdIn(script, k)))[0]
  ensures |Delays(Run(script))| >= k
  ensures Delays(Run(script))[k..] == Delays(Loop(script, k, RdIn(script, k)))
{
  Shift(script, 0, k);
  var s := Split(Run(script));
  assert s[k..][0] == s[k];
}

// The session of attempt k when it listens.
lemma ListenSession(script: seq<Attempt>, k: nat)
  requires k < |script| && script[k].Listens? && forall j :: 0 <= j < k ==> Continues(script[j])
  ensures SessionActions(script, k) == [Listen, InvalidateBoth] + RepeatAction(InvalidateBoth, script[k].notifications)
{
  Prefix(script, k);
  var rd := RdIn(script, k);
  var t := TryBlock(script[k], rd);
  TryBlockNoDelay(script[k], rd);
  var acts := [Listen, InvalidateBoth] + RepeatAction(InvalidateBoth, script[k].notifications);
  assert t.actions == acts;
  assert |acts| >= 2 && acts[|acts| - 1] != Return by {
    RepeatNoDelay(InvalidateBoth, script[k].notifications);
  }
  if script[k].lost {
    LoopStep(script, k, rd);
    assert Split(Loop(script, k, rd))[0] == acts;
  } else {
    assert Loop(script, k, rd) == acts + [Return];
    SplitNoDelayPrefix(acts, [Return]);
    assert Split([Return]) == [[Return]] by {
      assert [Return][1..] == [];
      assert Split([]) == [[]];
      var e: seq<seq<Action>> := [[]];
      assert e[1..] == [];
      assert [Return] + [] == [Return];
    }
    assert Split(Loop(script, k, rd))[0] == acts + [Return];
    assert (acts + [Return])[..|acts + [Return]| - 1] == acts;
  }
}

// The delay that follows attempt k, as the loop computes it.
lemma DelayValue(script: seq<Attempt>, k: nat)
  requires k < |script| && forall j :: 0 <= j <= k ==> Continues(script[j])
  ensures DelayAfter(script, k) == TryBlock(script[k], RdIn(script, k)).retryDelay as int
{
  Prefix(script, k);
  LoopStep(script, k, RdIn(script, k));
  var ds := Delays(Run(script));
  assert ds[k..][0] == ds[k];
}

lemma NoOverflow(rd: Int64)
  requires 0 < rd as int <= ThirtySecondTicks
  ensures NextRetryDelay(rd) as int == Min(2 * rd as int, ThirtySecondTicks)
{
  var x := rd as int * 2;
  assert 0 <= x + 0x8000_0000_0000_0000 < 0x1_0000_0000_0000_0000;
  assert (x + 0x8000_0000_0000_0000) % 0x1_0000_0000_0000_0000 == x + 0x8000_0000_0000_0000;
  assert WrapMul64(rd, 2) as int == x;
}

lemma MinPow(r: nat)
  ensures Min(2 * Min(OneSecondTicks * Pow2(r), ThirtySecondTicks), ThirtySecondTicks)
       == Min(OneSecondTicks * Pow2(r + 1), ThirtySecondTicks)
{
  assert Pow2(r + 1) == 2 * Pow2(r);
  assert OneSecondTicks * Pow2(r + 1) == 2 * (OneSecondTicks * Pow2(r));
}

lemma Pow2Pos(r: nat)
  ensures Pow2(r) >= 1
{
  if r > 0 { Pow2Pos(r - 1); }
}

// The retryDelay used after attempt k (no Continues needed: it is the loop variable).
lemma RdAfter(script: seq<Attempt>, k: nat)
  requires k < |script|
  ensures TryBlock(script[k], RdIn(script, k)).retryDelay as int
       == Min(OneSecondTicks * Pow2(RunLength(script, k)), ThirtySecondTicks)
  decreases k
{
  var rd := RdIn(script, k);
  TryBlockNoDelay(script[k], rd);
  if script[k].Listens? {
    assert Pow2(0) == 1;
  } else if k == 0 {
    assert Pow2(0) == 1;
  } else {
    RdAfter(script, k - 1);
    var prev := TryBlock(script[k - 1], RdIn(script, k - 1)).retryDelay;
    var r := RunLength(script, k - 1);
    Pow2Pos(r);
    assert 0 < prev as int <= ThirtySecondTicks;
    NoOverflow(prev);
    assert rd == NextRetryDelay(prev);
    MinPow(r);
  }
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen).
// ---------------------------------------------------------------------------------------------

// Citation: design CacheInvalidation.P1_ListeningWriteInvalidates: "Every revocation write and every tenant-status write issues `pg_notify` in the same transaction as the write … On every notification it invalidates both `TokenRevocationCache` and `TenantStatusCache`." (§2.3 lines 84–85)
lemma P1_ListeningWriteInvalidates(script: seq<Attempt>, k: nat)
  requires k < |script| && script[k].Listens? && forall j :: 0 <= j < k ==> Continues(script[j])
  ensures SessionActions(script, k) == [Listen, InvalidateBoth] + RepeatAction(InvalidateBoth, script[k].notifications)
{
  ListenSession(script, k);
}

// Citation: design CacheInvalidation.P2_NoWriteMissedAcrossReconnect: "On every connect and reconnect it issues `LISTEN` first, then invalidates both caches. A notification sent while it was disconnected is not replayed." (§2.3 line 86)
lemma P2_NoWriteMissedAcrossReconnect(script: seq<Attempt>, k: nat)
  requires k < |script| && script[k].Listens? && forall j :: 0 <= j < k ==> Continues(script[j])
  ensures |SessionActions(script, k)| >= 2 && SessionActions(script, k)[0] == Listen && SessionActions(script, k)[1] == InvalidateBoth
{
  ListenSession(script, k);
}

// Citation: "Reconnects back off from 1 s to 30 s." (§2.3 line 86)
lemma P3_BackoffFromOneToThirtySeconds(script: seq<Attempt>, k: nat)
  requires k < |script| && forall j :: 0 <= j <= k ==> Continues(script[j])
  ensures DelayAfter(script, k) == Min(OneSecondTicks * Pow2(RunLength(script, k)), ThirtySecondTicks)
{
  DelayValue(script, k);
  RdAfter(script, k);
}
