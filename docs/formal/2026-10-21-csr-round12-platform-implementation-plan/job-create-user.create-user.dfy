// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T15 S4 (4c), T19 S3 (3c–3e)
// Source: Iverson.Server/deploy/helm/iverson/charts/starrocks/templates/job-create-user.yaml the create-user container's sh script (62–111)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/job-create-user.create-user.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary (sh strings are byte sequences)
// ---------------------------------------------------------------------------

newtype Byte = x: int | 0 <= x < 256

datatype Option<T> = None | Some(value: T)

// The cluster as seen by the job's n-th mysql call: World(n).
datatype ClusterView = ClusterView(rootPassword: Option<seq<Byte>>, backendLines: seq<seq<Byte>>, frontendLines: seq<seq<Byte>>)

datatype Stmt = SelectOne | SetPassword | ShowBackends | ShowFrontends | Batch(lines: seq<seq<Byte>>) | Other

// ---------------------------------------------------------------------------
// Approved axioms (the mysql client)
// ---------------------------------------------------------------------------

datatype MysqlResult = MysqlResult(exit: int, out: seq<seq<Byte>>)

function MysqlRun(v: ClusterView, pw: Option<seq<Byte>>, stmt: Stmt): MysqlResult
  ensures pw != v.rootPassword ==> MysqlRun(v, pw, stmt).exit != 0
  ensures pw == v.rootPassword && stmt == ShowBackends ==> MysqlRun(v, pw, stmt) == MysqlResult(0, v.backendLines)
  ensures pw == v.rootPassword && stmt == ShowFrontends ==> MysqlRun(v, pw, stmt) == MysqlResult(0, v.frontendLines)

// ---------------------------------------------------------------------------
// Helpers and constants
// ---------------------------------------------------------------------------

// One mysql call: the password it logs in with (None: no -p), and its statement.
type Call = (Option<seq<Byte>>, Stmt)

// The bytes of an ASCII literal (every literal below is ASCII, so the modulus never changes a unit).
function Bytes(s: string): seq<Byte>
  ensures |Bytes(s)| == |s|
{
  seq(|s|, i requires 0 <= i < |s| => (s[i] as int % 256) as Byte)
}

lemma BytesAt(s: string, t: nat)
  requires t < |s|
  ensures Bytes(s)[t] == (s[t] as int % 256) as Byte
{
}

predicate IsPrefix(p: seq<Byte>, s: seq<Byte>)
{
  |p| <= |s| && s[..|p|] == p
}

// Secret values injected through env (APP_PW, PROVISIONER_PW): arbitrary byte strings.
const AppPw: seq<Byte>
const ProvisionerPw: seq<Byte>

// A run whose until-loops never exit is infinite; the model keeps its first Fuel calls.
// Fuel is uninterpreted, so every lemma holds for every prefix length.
const Fuel: nat

// The batch of lines 99-111, one element per statement line, after expansion of $APP_PW and
// $PROVISIONER_PW, with the YAML block's two-space statement indentation and the blank
// first/last lines of the quoted string dropped.
const CreateAppUser: seq<Byte> := Bytes("CREATE " + "USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY '") + AppPw + Bytes("';")
const CreateProvisionerUser: seq<Byte> := Bytes("CREATE " + "USER IF NOT EXISTS 'iverson_provisioner'@'%' IDENTIFIED BY '") + ProvisionerPw + Bytes("';")
const CreateDatabase: seq<Byte> := Bytes("CREATE DATABASE IF NOT EXISTS iverson;")
const GrantCreateDatabase: seq<Byte> := Bytes("GRANT CREATE " + "DATABASE ON CATALOG default_catalog TO 'iverson_provisioner'@'%';")
const GrantUserAdmin: seq<Byte> := Bytes("GRANT user_admin TO 'iverson_provisioner'@'%';")
const RevokeDml: seq<Byte> := Bytes("REVOKE " + "SELECT, INSERT, UPDATE, DELETE ON iverson.* FROM 'iverson_app'@'%';")
const RevokeDdl: seq<Byte> := Bytes("REVOKE " + "CREATE TABLE, DROP, ALTER ON DATABASE iverson FROM 'iverson_app'@'%';")
const RevokeCreateDatabase: seq<Byte> := Bytes("REVOKE " + "CREATE DATABASE ON CATALOG default_catalog FROM 'iverson_app'@'%';")
const RevokeUserAdmin: seq<Byte> := Bytes("REVOKE user_admin FROM 'iverson_app'@'%';")
const RevokeOperate: seq<Byte> := Bytes("REVOKE " + "OPERATE ON SYSTEM FROM USER 'iverson_app'@'%';")
const AlterAppUser: seq<Byte> := Bytes("ALTER " + "USER 'iverson_app' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296');")

const GrantOperatePrefix: seq<Byte> := Bytes("GRANT OPERATE")

const BatchLines: seq<seq<Byte>> := [
  CreateAppUser,          // :100
  CreateProvisionerUser,  // :101
  CreateDatabase,         // :102
  GrantCreateDatabase,    // :103
  GrantUserAdmin,         // :104
  RevokeDml,              // :105
  RevokeDdl,              // :106
  RevokeCreateDatabase,   // :107
  RevokeUserAdmin,        // :108
  RevokeOperate,          // :109
  AlterAppUser            // :110
]

// ---------------------------------------------------------------------------
// Transliteration of lines 62-111
// ---------------------------------------------------------------------------

// fe() (:73-76): root's login, with -p"$ROOT_PW" iff ROOT_HAS_PW = 1.
function Fe(rootHasPw: bool, rootPw: seq<Byte>): Option<seq<Byte>>
{
  if rootHasPw then Some(rootPw) else None
}

// The cluster after the job's own SET PASSWORD succeeded: root's password is ROOT_PW.
function ViewAfterSet(world: nat -> ClusterView, rootPw: seq<Byte>, n: nat): ClusterView
{
  world(n).(rootPassword := Some(rootPw))
}

// :62-65 set -e, FE_HOST, FE_REPLICAS, BE_REPLICAS; the run starts at call 0.
function JobCalls(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat): seq<Call>
{
  ProbeLoop(world, rootPw, beReplicas, feReplicas, 0, Fuel)
}

// :77-87 until <probe>; do sleep; done. n is the index of the next mysql call.
function ProbeLoop(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, n: nat, fuel: nat): seq<Call>
  decreases fuel
{
  if fuel == 0 then []
  // :78 mysql ... -u root -p"$ROOT_PW" -e "SELECT 1"
  else if MysqlRun(world(n), Some(rootPw), SelectOne).exit == 0 then
    // :79 ROOT_HAS_PW=1; the until condition is true
    [(Some(rootPw), SelectOne)] + SetPasswordStep(world, rootPw, beReplicas, feReplicas, true, n + 1, fuel - 1)
  else if fuel == 1 then [(Some(rootPw), SelectOne)]
  // :80 mysql ... -u root -e "SELECT 1"
  else if MysqlRun(world(n + 1), None, SelectOne).exit == 0 then
    // :81 ROOT_HAS_PW=0; the until condition is true
    [(Some(rootPw), SelectOne), (None, SelectOne)] + SetPasswordStep(world, rootPw, beReplicas, feReplicas, false, n + 2, fuel - 2)
  else
    // :83 false; :86 echo, sleep; loop
    [(Some(rootPw), SelectOne), (None, SelectOne)] + ProbeLoop(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2)
}

// :93 fe -e "SET PASSWORD ..."; under set -e a failure ends the script. :94 ROOT_HAS_PW=1.
function SetPasswordStep(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, rootHasPw: bool, n: nat, fuel: nat): seq<Call>
  decreases fuel
{
  if fuel == 0 then []
  else if MysqlRun(world(n), Fe(rootHasPw, rootPw), SetPassword).exit != 0 then
    [(Fe(rootHasPw, rootPw), SetPassword)]
  else
    [(Fe(rootHasPw, rootPw), SetPassword)] + WaitLoop(world, rootPw, beReplicas, feReplicas, n + 1, fuel - 1)
}

// :95-98 until [ backends -ge BE_REPLICAS ] && [ frontends -ge FE_REPLICAS ]; do sleep; done.
// `fe ... 2>/dev/null | wc -l`: the line count of mysql's stdout, whatever mysql's exit.
function WaitLoop(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, n: nat, fuel: nat): seq<Call>
  decreases fuel
{
  if fuel == 0 then []
  else if !(|MysqlRun(ViewAfterSet(world, rootPw, n), Fe(true, rootPw), ShowBackends).out| >= beReplicas) then
    // first [ ] false; && short-circuits; the until condition is false
    [(Fe(true, rootPw), ShowBackends)] + WaitLoop(world, rootPw, beReplicas, feReplicas, n + 1, fuel - 1)
  else if fuel == 1 then [(Fe(true, rootPw), ShowBackends)]
  else if !(|MysqlRun(ViewAfterSet(world, rootPw, n + 1), Fe(true, rootPw), ShowFrontends).out| >= feReplicas) then
    [(Fe(true, rootPw), ShowBackends), (Fe(true, rootPw), ShowFrontends)] + WaitLoop(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2)
  else
    [(Fe(true, rootPw), ShowBackends), (Fe(true, rootPw), ShowFrontends)] + BatchStep(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2)
}

// :99-111 fe -e "<batch>". The model ends at line 111 whatever the batch's exit.
function BatchStep(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, n: nat, fuel: nat): seq<Call>
{
  if fuel == 0 then [] else [(Fe(true, rootPw), Batch(BatchLines))]
}

// ---------------------------------------------------------------------------
// Property vocabulary
// ---------------------------------------------------------------------------

// Some SELECT 1 probe succeeded: the job moved on from a probe to a call that is not a probe.
predicate FeAnswered(calls: seq<Call>)
{
  exists i :: 0 <= i && i + 1 < |calls| && calls[i].1 == SelectOne && calls[i + 1].1 != SelectOne
}

// The number of leading probe calls (the FE-wait loop of :77-87).
function LeadSel(calls: seq<Call>): nat
  ensures LeadSel(calls) <= |calls|
{
  if |calls| > 0 && calls[0].1 == SelectOne then 1 + LeadSel(calls[1..]) else 0
}

// The calls after the FE-wait loop.
function AfterProbe(calls: seq<Call>): seq<Call>
{
  calls[LeadSel(calls)..]
}

// ---------------------------------------------------------------------------
// Helper predicates and lemmas
// ---------------------------------------------------------------------------

predicate Guarded(calls: seq<Call>, world: nat -> ClusterView, n: nat, beReplicas: nat, feReplicas: nat)
{
  forall i :: 0 <= i < |calls| && calls[i].1.Batch? ==>
    exists j: nat, k: nat :: (j < i && k < i
      && calls[j].1 == ShowBackends && |world(n + j).backendLines| >= beReplicas
      && calls[k].1 == ShowFrontends && |world(n + k).frontendLines| >= feReplicas)
}

predicate BatchesAreBatchLines(calls: seq<Call>)
{
  forall i :: 0 <= i < |calls| && calls[i].1.Batch? ==> calls[i].1.lines == BatchLines
}

predicate AllRoot(calls: seq<Call>, rootPw: seq<Byte>)
{
  forall j :: 0 <= j < |calls| ==> calls[j].0 == Some(rootPw)
}

predicate NoSetPassword(calls: seq<Call>)
{
  forall j :: 0 <= j < |calls| ==> calls[j].1 != SetPassword
}

predicate PwAfterSet(calls: seq<Call>, rootPw: seq<Byte>)
{
  forall i, j :: 0 <= i < j < |calls| && calls[i].1 == SetPassword ==> calls[j].0 == Some(rootPw)
}

lemma GuardedCons(c: Call, xs: seq<Call>, world: nat -> ClusterView, n: nat, beReplicas: nat, feReplicas: nat)
  requires !c.1.Batch?
  requires Guarded(xs, world, n + 1, beReplicas, feReplicas)
  ensures Guarded([c] + xs, world, n, beReplicas, feReplicas)
{
  var ys := [c] + xs;
  forall i | 0 <= i < |ys| && ys[i].1.Batch?
    ensures exists j: nat, k: nat :: (j < i && k < i
      && ys[j].1 == ShowBackends && |world(n + j).backendLines| >= beReplicas
      && ys[k].1 == ShowFrontends && |world(n + k).frontendLines| >= feReplicas)
  {
    assert i > 0;
    assert ys[i] == xs[i - 1];
    var j: nat, k: nat :| j < i - 1 && k < i - 1
      && xs[j].1 == ShowBackends && |world(n + 1 + j).backendLines| >= beReplicas
      && xs[k].1 == ShowFrontends && |world(n + 1 + k).frontendLines| >= feReplicas;
    assert ys[j + 1] == xs[j];
    assert ys[k + 1] == xs[k];
    assert n + (j + 1) == n + 1 + j;
    assert n + (k + 1) == n + 1 + k;
  }
}

lemma PwAfterSetCons(c: Call, xs: seq<Call>, rootPw: seq<Byte>)
  requires c.1 != SetPassword
  requires PwAfterSet(xs, rootPw)
  ensures PwAfterSet([c] + xs, rootPw)
{
  var ys := [c] + xs;
  forall i, j | 0 <= i < j < |ys| && ys[i].1 == SetPassword
    ensures ys[j].0 == Some(rootPw)
  {
    assert i > 0;
    assert ys[i] == xs[i - 1];
    assert ys[j] == xs[j - 1];
  }
}

lemma AfterProbeCons(c: Call, xs: seq<Call>)
  requires c.1 == SelectOne
  ensures AfterProbe([c] + xs) == AfterProbe(xs)
{
  assert ([c] + xs)[1..] == xs;
}

lemma WaitLoopFacts(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, n: nat, fuel: nat)
  ensures Guarded(WaitLoop(world, rootPw, beReplicas, feReplicas, n, fuel), world, n, beReplicas, feReplicas)
  ensures BatchesAreBatchLines(WaitLoop(world, rootPw, beReplicas, feReplicas, n, fuel))
  ensures AllRoot(WaitLoop(world, rootPw, beReplicas, feReplicas, n, fuel), rootPw)
  ensures NoSetPassword(WaitLoop(world, rootPw, beReplicas, feReplicas, n, fuel))
  decreases fuel
{
  var cb: Call := (Fe(true, rootPw), ShowBackends);
  var cf: Call := (Fe(true, rootPw), ShowFrontends);
  var w := WaitLoop(world, rootPw, beReplicas, feReplicas, n, fuel);
  if fuel == 0 {
  } else if !(|MysqlRun(ViewAfterSet(world, rootPw, n), Fe(true, rootPw), ShowBackends).out| >= beReplicas) {
    var rest := WaitLoop(world, rootPw, beReplicas, feReplicas, n + 1, fuel - 1);
    WaitLoopFacts(world, rootPw, beReplicas, feReplicas, n + 1, fuel - 1);
    assert w == [cb] + rest;
    GuardedCons(cb, rest, world, n, beReplicas, feReplicas);
  } else if fuel == 1 {
    assert w == [cb];
  } else if !(|MysqlRun(ViewAfterSet(world, rootPw, n + 1), Fe(true, rootPw), ShowFrontends).out| >= feReplicas) {
    var rest := WaitLoop(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    WaitLoopFacts(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    assert w == [cb] + ([cf] + rest);
    GuardedCons(cf, rest, world, n + 1, beReplicas, feReplicas);
    GuardedCons(cb, [cf] + rest, world, n, beReplicas, feReplicas);
  } else {
    var v0 := ViewAfterSet(world, rootPw, n);
    var v1 := ViewAfterSet(world, rootPw, n + 1);
    assert MysqlRun(v0, Some(rootPw), ShowBackends) == MysqlResult(0, v0.backendLines);
    assert MysqlRun(v1, Some(rootPw), ShowFrontends) == MysqlResult(0, v1.frontendLines);
    assert |world(n + 0).backendLines| >= beReplicas;
    assert |world(n + 1).frontendLines| >= feReplicas;
    var b := BatchStep(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    assert w == [cb, cf] + b;
    if fuel - 2 == 0 {
      assert w == [cb, cf];
    } else {
      assert w == [cb, cf, (Fe(true, rootPw), Batch(BatchLines))];
      forall i | 0 <= i < |w| && w[i].1.Batch?
        ensures exists j: nat, k: nat :: (j < i && k < i
          && w[j].1 == ShowBackends && |world(n + j).backendLines| >= beReplicas
          && w[k].1 == ShowFrontends && |world(n + k).frontendLines| >= feReplicas)
      {
        assert i == 2;
        var j: nat, k: nat := 0, 1;
        assert w[j].1 == ShowBackends && |world(n + j).backendLines| >= beReplicas;
        assert w[k].1 == ShowFrontends && |world(n + k).frontendLines| >= feReplicas;
      }
    }
  }
}

lemma SetPasswordStepFacts(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, rootHasPw: bool, n: nat, fuel: nat)
  ensures Guarded(SetPasswordStep(world, rootPw, beReplicas, feReplicas, rootHasPw, n, fuel), world, n, beReplicas, feReplicas)
  ensures BatchesAreBatchLines(SetPasswordStep(world, rootPw, beReplicas, feReplicas, rootHasPw, n, fuel))
  ensures PwAfterSet(SetPasswordStep(world, rootPw, beReplicas, feReplicas, rootHasPw, n, fuel), rootPw)
  ensures var s := SetPasswordStep(world, rootPw, beReplicas, feReplicas, rootHasPw, n, fuel);
    |s| > 0 ==> s[0].1 == SetPassword
{
  var s := SetPasswordStep(world, rootPw, beReplicas, feReplicas, rootHasPw, n, fuel);
  var c: Call := (Fe(rootHasPw, rootPw), SetPassword);
  if fuel == 0 {
  } else if MysqlRun(world(n), Fe(rootHasPw, rootPw), SetPassword).exit != 0 {
    assert s == [c];
  } else {
    var rest := WaitLoop(world, rootPw, beReplicas, feReplicas, n + 1, fuel - 1);
    WaitLoopFacts(world, rootPw, beReplicas, feReplicas, n + 1, fuel - 1);
    assert s == [c] + rest;
    GuardedCons(c, rest, world, n, beReplicas, feReplicas);
    forall i, j | 0 <= i < j < |s| && s[i].1 == SetPassword
      ensures s[j].0 == Some(rootPw)
    {
      assert s[j] == rest[j - 1];
    }
  }
}

lemma ProbeLoopFacts(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, n: nat, fuel: nat)
  ensures Guarded(ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel), world, n, beReplicas, feReplicas)
  ensures BatchesAreBatchLines(ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel))
  ensures PwAfterSet(ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel), rootPw)
  ensures var a := AfterProbe(ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel));
    |a| > 0 ==> a[0].1 == SetPassword
{
  ProbeGuarded(world, rootPw, beReplicas, feReplicas, n, fuel);
  ProbeBatchesAndPw(world, rootPw, beReplicas, feReplicas, n, fuel);
  ProbeAfter(world, rootPw, beReplicas, feReplicas, n, fuel);
}

lemma ProbeGuarded(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, n: nat, fuel: nat)
  ensures Guarded(ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel), world, n, beReplicas, feReplicas)
  decreases fuel
{
  var p := ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel);
  var c1: Call := (Some(rootPw), SelectOne);
  var c2: Call := (None, SelectOne);
  if fuel == 0 {
  } else if MysqlRun(world(n), Some(rootPw), SelectOne).exit == 0 {
    var rest := SetPasswordStep(world, rootPw, beReplicas, feReplicas, true, n + 1, fuel - 1);
    SetPasswordStepFacts(world, rootPw, beReplicas, feReplicas, true, n + 1, fuel - 1);
    assert p == [c1] + rest;
    GuardedCons(c1, rest, world, n, beReplicas, feReplicas);
  } else if fuel == 1 {
    assert p == [c1];
  } else if MysqlRun(world(n + 1), None, SelectOne).exit == 0 {
    var rest := SetPasswordStep(world, rootPw, beReplicas, feReplicas, false, n + 2, fuel - 2);
    SetPasswordStepFacts(world, rootPw, beReplicas, feReplicas, false, n + 2, fuel - 2);
    assert p == [c1] + ([c2] + rest);
    GuardedCons(c2, rest, world, n + 1, beReplicas, feReplicas);
    GuardedCons(c1, [c2] + rest, world, n, beReplicas, feReplicas);
  } else {
    var rest := ProbeLoop(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    ProbeGuarded(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    assert p == [c1] + ([c2] + rest);
    GuardedCons(c2, rest, world, n + 1, beReplicas, feReplicas);
    GuardedCons(c1, [c2] + rest, world, n, beReplicas, feReplicas);
  }
}

lemma BatchesCons(c: Call, xs: seq<Call>)
  requires !c.1.Batch?
  requires BatchesAreBatchLines(xs)
  ensures BatchesAreBatchLines([c] + xs)
{
  var ys := [c] + xs;
  forall i | 0 <= i < |ys| && ys[i].1.Batch?
    ensures ys[i].1.lines == BatchLines
  {
    assert ys[i] == xs[i - 1];
  }
}

lemma ProbeBatchesAndPw(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, n: nat, fuel: nat)
  ensures BatchesAreBatchLines(ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel))
  ensures PwAfterSet(ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel), rootPw)
  decreases fuel
{
  var p := ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel);
  var c1: Call := (Some(rootPw), SelectOne);
  var c2: Call := (None, SelectOne);
  if fuel == 0 {
  } else if MysqlRun(world(n), Some(rootPw), SelectOne).exit == 0 {
    var rest := SetPasswordStep(world, rootPw, beReplicas, feReplicas, true, n + 1, fuel - 1);
    SetPasswordStepFacts(world, rootPw, beReplicas, feReplicas, true, n + 1, fuel - 1);
    assert p == [c1] + rest;
    BatchesCons(c1, rest);
    PwAfterSetCons(c1, rest, rootPw);
  } else if fuel == 1 {
    assert p == [c1];
  } else if MysqlRun(world(n + 1), None, SelectOne).exit == 0 {
    var rest := SetPasswordStep(world, rootPw, beReplicas, feReplicas, false, n + 2, fuel - 2);
    SetPasswordStepFacts(world, rootPw, beReplicas, feReplicas, false, n + 2, fuel - 2);
    assert p == [c1] + ([c2] + rest);
    BatchesCons(c2, rest);
    BatchesCons(c1, [c2] + rest);
    PwAfterSetCons(c2, rest, rootPw);
    PwAfterSetCons(c1, [c2] + rest, rootPw);
  } else {
    var rest := ProbeLoop(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    ProbeBatchesAndPw(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    assert p == [c1] + ([c2] + rest);
    BatchesCons(c2, rest);
    BatchesCons(c1, [c2] + rest);
    PwAfterSetCons(c2, rest, rootPw);
    PwAfterSetCons(c1, [c2] + rest, rootPw);
  }
}

lemma ProbeAfter(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, n: nat, fuel: nat)
  ensures var a := AfterProbe(ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel));
    |a| > 0 ==> a[0].1 == SetPassword
  decreases fuel
{
  var p := ProbeLoop(world, rootPw, beReplicas, feReplicas, n, fuel);
  var c1: Call := (Some(rootPw), SelectOne);
  var c2: Call := (None, SelectOne);
  if fuel == 0 {
  } else if MysqlRun(world(n), Some(rootPw), SelectOne).exit == 0 {
    var rest := SetPasswordStep(world, rootPw, beReplicas, feReplicas, true, n + 1, fuel - 1);
    SetPasswordStepFacts(world, rootPw, beReplicas, feReplicas, true, n + 1, fuel - 1);
    assert p == [c1] + rest;
    AfterProbeCons(c1, rest);
    assert LeadSel(rest) == 0;
  } else if fuel == 1 {
    assert p == [c1];
    assert LeadSel(p) == 1 + LeadSel(p[1..]);
    assert p[1..] == [];
  } else if MysqlRun(world(n + 1), None, SelectOne).exit == 0 {
    var rest := SetPasswordStep(world, rootPw, beReplicas, feReplicas, false, n + 2, fuel - 2);
    SetPasswordStepFacts(world, rootPw, beReplicas, feReplicas, false, n + 2, fuel - 2);
    assert p == [c1] + ([c2] + rest);
    AfterProbeCons(c2, rest);
    AfterProbeCons(c1, [c2] + rest);
    assert LeadSel(rest) == 0;
  } else {
    var rest := ProbeLoop(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    ProbeAfter(world, rootPw, beReplicas, feReplicas, n + 2, fuel - 2);
    assert p == [c1] + ([c2] + rest);
    AfterProbeCons(c2, rest);
    AfterProbeCons(c1, [c2] + rest);
  }
}

lemma NotPrefixAt(p: seq<Byte>, s: seq<Byte>, t: nat)
  requires t < |p| && t < |s| && s[t] != p[t]
  ensures !IsPrefix(p, s)
{
  if |p| <= |s| {
    assert s[..|p|][t] == s[t];
    assert s[..|p|] != p;
  }
}

lemma BatchLinesFacts()
  ensures BatchLines[0] == CreateAppUser
  ensures BatchLines[9] == RevokeOperate
  ensures forall l :: l in BatchLines ==> !IsPrefix(GrantOperatePrefix, l)
{
  var g := GrantOperatePrefix;
  BytesAt("GRANT OPERATE", 0);
  BytesAt("GRANT OPERATE", 6);
  assert g[0] == 71 && g[6] == 79; // 'G', 'O'
  var cau := "CREATE " + "USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY '";
  BytesAt(cau, 0);
  assert CreateAppUser[0] == Bytes(cau)[0] == 67; // 'C'
  NotPrefixAt(g, CreateAppUser, 0);
  var cpu := "CREATE " + "USER IF NOT EXISTS 'iverson_provisioner'@'%' IDENTIFIED BY '";
  BytesAt(cpu, 0);
  assert CreateProvisionerUser[0] == Bytes(cpu)[0] == 67;
  NotPrefixAt(g, CreateProvisionerUser, 0);
  BytesAt("CREATE DATABASE IF NOT EXISTS iverson;", 0);
  NotPrefixAt(g, CreateDatabase, 0);
  BytesAt("GRANT CREATE " + "DATABASE ON CATALOG default_catalog TO 'iverson_provisioner'@'%';", 6);
  assert GrantCreateDatabase[6] == 67;          // 'C'
  NotPrefixAt(g, GrantCreateDatabase, 6);
  BytesAt("GRANT user_admin TO 'iverson_provisioner'@'%';", 6);
  assert GrantUserAdmin[6] == 117;              // 'u'
  NotPrefixAt(g, GrantUserAdmin, 6);
  BytesAt("REVOKE " + "SELECT, INSERT, UPDATE, DELETE ON iverson.* FROM 'iverson_app'@'%';", 0);
  NotPrefixAt(g, RevokeDml, 0);
  BytesAt("REVOKE " + "CREATE TABLE, DROP, ALTER ON DATABASE iverson FROM 'iverson_app'@'%';", 0);
  NotPrefixAt(g, RevokeDdl, 0);
  BytesAt("REVOKE " + "CREATE DATABASE ON CATALOG default_catalog FROM 'iverson_app'@'%';", 0);
  NotPrefixAt(g, RevokeCreateDatabase, 0);
  BytesAt("REVOKE user_admin FROM 'iverson_app'@'%';", 0);
  NotPrefixAt(g, RevokeUserAdmin, 0);
  BytesAt("REVOKE " + "OPERATE ON SYSTEM FROM USER 'iverson_app'@'%';", 0);
  NotPrefixAt(g, RevokeOperate, 0);
  BytesAt("ALTER " + "USER 'iverson_app' SET PROPERTIES ('session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296');", 0);
  NotPrefixAt(g, AlterAppUser, 0);
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: design StarRocksBootstrap.P2_JobSetsPasswordOnFe0: "the create-user Job sets root's password as soon as FE-0 answers, no longer waiting for the BE count first" (§4.3 line 375)
lemma P1_JobSetsPasswordOnFe0(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat)
  requires FeAnswered(JobCalls(world, rootPw, beReplicas, feReplicas))
  requires |AfterProbe(JobCalls(world, rootPw, beReplicas, feReplicas))| > 0
  ensures AfterProbe(JobCalls(world, rootPw, beReplicas, feReplicas))[0].1 == SetPassword
{
  ProbeLoopFacts(world, rootPw, beReplicas, feReplicas, 0, Fuel);
}

// Citation: "It then waits for `BE_REPLICAS` backends and every FE before the rest of the batch." (§4.3 line 375); plan check `t19_job.sh` (T19 S1: `show-backends` and `show-frontends` before `create-user`), generalised
lemma P2_BatchOnlyAfterEveryNodeListed(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, i: nat)
  requires i < |JobCalls(world, rootPw, beReplicas, feReplicas)| && JobCalls(world, rootPw, beReplicas, feReplicas)[i].1.Batch?
  ensures exists j: nat, k: nat :: j < i && k < i
            && JobCalls(world, rootPw, beReplicas, feReplicas)[j].1 == ShowBackends && |world(j).backendLines| >= beReplicas
            && JobCalls(world, rootPw, beReplicas, feReplicas)[k].1 == ShowFrontends && |world(k).frontendLines| >= feReplicas
{
  var calls := JobCalls(world, rootPw, beReplicas, feReplicas);
  ProbeLoopFacts(world, rootPw, beReplicas, feReplicas, 0, Fuel);
  var j: nat, k: nat :| j < i && k < i
    && calls[j].1 == ShowBackends && |world(0 + j).backendLines| >= beReplicas
    && calls[k].1 == ShowFrontends && |world(0 + k).frontendLines| >= feReplicas;
  assert 0 + j == j && 0 + k == k;
}

// Citation: "`GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%'` is dropped … the job and compose's `starrocks-init` also run `REVOKE OPERATE ON SYSTEM FROM USER 'iverson_app'@'%'` beside the existing role revoke, after `CREATE USER IF NOT EXISTS`" (§3.5 line 319); plan check (T15 S5: `"GRANT OPERATE" not in text`)
lemma P3_RevokeOperateAfterCreateUser(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, i: nat)
  requires i < |JobCalls(world, rootPw, beReplicas, feReplicas)| && JobCalls(world, rootPw, beReplicas, feReplicas)[i].1.Batch?
  ensures exists a, b :: 0 <= a < b < |JobCalls(world, rootPw, beReplicas, feReplicas)[i].1.lines|
            && JobCalls(world, rootPw, beReplicas, feReplicas)[i].1.lines[a] == CreateAppUser
            && JobCalls(world, rootPw, beReplicas, feReplicas)[i].1.lines[b] == RevokeOperate
  ensures forall l :: l in JobCalls(world, rootPw, beReplicas, feReplicas)[i].1.lines ==> !IsPrefix(GrantOperatePrefix, l)
{
  ProbeLoopFacts(world, rootPw, beReplicas, feReplicas, 0, Fuel);
  BatchLinesFacts();
  var lines := JobCalls(world, rootPw, beReplicas, feReplicas)[i].1.lines;
  assert lines == BatchLines;
  assert 0 <= 0 < 9 < |lines| && lines[0] == CreateAppUser && lines[9] == RevokeOperate;
}

// Citation: "Because the FE and BE then log in with root's password from their first start, the create-user Job sets root's password as soon as FE-0 answers" (§4.3 line 375); plan check `t19_job.sh` (`[ "$(command grep -c "^pw" $D/log)" -ge 3 ]`, state `r` after both runs)
lemma P4_PasswordAfterSetPassword(world: nat -> ClusterView, rootPw: seq<Byte>, beReplicas: nat, feReplicas: nat, i: nat, j: nat)
  requires i < j < |JobCalls(world, rootPw, beReplicas, feReplicas)| && JobCalls(world, rootPw, beReplicas, feReplicas)[i].1 == SetPassword
  ensures JobCalls(world, rootPw, beReplicas, feReplicas)[j].0 == Some(rootPw)
{
  ProbeLoopFacts(world, rootPw, beReplicas, feReplicas, 0, Fuel);
}
