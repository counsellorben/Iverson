// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a11e848d722d05cc808840ae796e6afdb2101aa2
// Source: §4.3 #3, lines 375-375
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/StarRocksBootstrap.dfy
// Round: 3

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// A StarRocks pod: FE(0) is FE-0 (the pod-0 leader), FE(i) for i > 0 is an FE
// follower, BE(i) is a backend.
datatype PodId = FE(i: nat) | BE(i: nat)

predicate IsFe0(pod: PodId) { pod.FE? && pod.i == 0 }

// Phases of the create-user Job. The new order (line 375):
//   WaitingForFe0 --(FE-0 answers; SET PASSWORD)--> WaitingForBackendsAndFes
//   --(BE_REPLICAS backends and every FE)--> RestOfBatch --> Done
// The old order (job-create-user.yaml:87-96):
//   WaitingForFe0 --(FE-0 answers)--> WaitingForBackends
//   --(BE_REPLICAS backends; SET PASSWORD + rest of batch)--> Done
datatype JobPhase =
  | WaitingForFe0
  | WaitingForBackends
  | WaitingForBackendsAndFes
  | RestOfBatch
  | Done

datatype InstallState = InstallState(
  rootPasswordSet: bool,   // has the Job run SET PASSWORD for root
  fe0Answers: bool,        // FE-0 answers the Job's login probe
  job: JobPhase,
  beReplicas: nat,         // BE_REPLICAS
  feReplicas: nat,         // number of FE pods ("every FE")
  joined: set<PodId>       // pods registered with the cluster (FE-0 as leader)
)

datatype Event = Start(pod: PodId) | JobTick

predicate Joined(s: InstallState, pod: PodId) { pod in s.joined }

function BeCount(s: InstallState): nat { |set p | p in s.joined && p.BE?| }

function FeCount(s: InstallState): nat { |set p | p in s.joined && p.FE?| }

predicate NoBeJoined(s: InstallState) { forall p :: p in s.joined ==> !p.BE? }

// VA64: BE refused (62 × ERROR 1045) "exited 1 at 61 s, unregistered; after SET PASSWORD a restart registered it, Alive: true"; follower "exited 1 at 120 s without ADD FOLLOWER; a restart after the password joined it"; "FE-0 on a fresh install never logs in"
predicate JoinSucceeds(rootPasswordSet: bool)
  ensures JoinSucceeds(rootPasswordSet) <==> rootPasswordSet

// A pod start (or restart: a restart is a fresh start of the entrypoint).
// FE-0 never logs in and starts as leader (VA64), so it always comes up and
// answers. A BE or FE follower logs in with MYSQL_PWD = root-password: it
// joins iff the login succeeds; otherwise it "exits 1, unregistered" — the
// state is unchanged.
function StartPod(s: InstallState, pod: PodId): InstallState
{
  if IsFe0(pod) then
    s.(fe0Answers := true, joined := s.joined + {pod})
  else if JoinSucceeds(s.rootPasswordSet) then
    s.(joined := s.joined + {pod})
  else
    s
}

// One step of the create-user Job in the new order (line 375).
function JobStep(s: InstallState): InstallState
{
  match s.job
  case WaitingForFe0 =>
    // "sets root's password as soon as FE-0 answers"
    if s.fe0Answers then s.(rootPasswordSet := true, job := WaitingForBackendsAndFes)
    else s
  case WaitingForBackends =>
    // not a phase of the new order; the new Job never enters it
    s
  case WaitingForBackendsAndFes =>
    // "It then waits for BE_REPLICAS backends and every FE before the rest of the batch."
    if BeCount(s) >= s.beReplicas && FeCount(s) >= s.feReplicas then s.(job := RestOfBatch)
    else s
  case RestOfBatch => s.(job := Done)
  case Done => s
}

// One step of the create-user Job in the old order (job-create-user.yaml:87-96):
// wait for FE, then wait for BE_REPLICAS backends, then SET PASSWORD + batch.
function OldJobStep(s: InstallState): InstallState
{
  match s.job
  case WaitingForFe0 =>
    if s.fe0Answers then s.(job := WaitingForBackends) else s
  case WaitingForBackends =>
    if BeCount(s) >= s.beReplicas then s.(rootPasswordSet := true, job := Done)
    else s
  case WaitingForBackendsAndFes => s
  case RestOfBatch => s
  case Done => s
}

// The old Job is in one of its phases before SET PASSWORD.
predicate OldJobOrder(s: InstallState)
{
  s.job == WaitingForFe0 || s.job == WaitingForBackends
}

function ApplyOld(s: InstallState, e: Event): InstallState
{
  match e
  case Start(pod) => StartPod(s, pod)
  case JobTick => OldJobStep(s)
}

// Any interleaving of pod starts/restarts and old-order Job steps,
// with the pods carrying MYSQL_PWD.
function RunOld(s: InstallState, steps: seq<Event>): InstallState
  decreases |steps|
{
  if |steps| == 0 then s else RunOld(ApplyOld(s, steps[0]), steps[1..])
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

predicate OldStuck(s: InstallState)
{
  !s.rootPasswordSet && OldJobOrder(s) && NoBeJoined(s) && s.beReplicas > 0
}

lemma NoBeMeansZeroCount(s: InstallState)
  requires NoBeJoined(s)
  ensures BeCount(s) == 0
{
  var bes := set p | p in s.joined && p.BE?;
  assert bes == {};
}

lemma ApplyOldPreservesStuck(s: InstallState, e: Event)
  requires OldStuck(s)
  ensures OldStuck(ApplyOld(s, e))
{
  match e
  case Start(pod) =>
    assert !JoinSucceeds(s.rootPasswordSet);
  case JobTick =>
    NoBeMeansZeroCount(s);
}

lemma RunOldPreservesStuck(s: InstallState, steps: seq<Event>)
  requires OldStuck(s)
  ensures OldStuck(RunOld(s, steps))
  decreases |steps|
{
  if |steps| > 0 {
    ApplyOldPreservesStuck(s, steps[0]);
    RunOldPreservesStuck(ApplyOld(s, steps[0]), steps[1..]);
  }
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "A BE or FE follower that starts before the password is set exits 1, unregistered … and joins on its first restart after it (Verified assumption 64)." (§4.3 line 375)
lemma P1_StartAfterPasswordJoins(s: InstallState, pod: PodId)
  requires s.rootPasswordSet
  ensures Joined(StartPod(s, pod), pod)
{
}

// Citation: "the create-user Job sets root's password as soon as FE-0 answers, no longer waiting for the BE count first" (§4.3 line 375)
lemma P2_JobSetsPasswordOnFe0(s: InstallState)
  requires s.fe0Answers && !s.rootPasswordSet && s.job == WaitingForFe0
  ensures JobStep(s).rootPasswordSet
{
}

// Citation: "that wait, `job-create-user.yaml:87-96`, existed only because the BE logged in without one" (§4.3 line 375)
lemma P3_OldOrderDeadlocks(s: InstallState, steps: seq<Event>)
  requires !s.rootPasswordSet && OldJobOrder(s) && NoBeJoined(s) && s.beReplicas > 0
  ensures !RunOld(s, steps).rootPasswordSet
{
  RunOldPreservesStuck(s, steps);
}
