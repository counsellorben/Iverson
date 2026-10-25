// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T25 S3
// Source: Iverson.Server/deploy/scripts/mesh-check.sh UNENROLLED_PY (59–66)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/mesh-check.UNENROLLED_PY.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

newtype CodePoint = x: int | 0 <= x < 0x110000

// One element of `json.load(sys.stdin)["items"]`, reduced to the fields the program reads:
//   name        = p["metadata"]["name"]
//   phase       = p["status"].get("phase")                       (None when the key is absent)
//   redirection = (p["metadata"].get("annotations") or {}).get("ambient.istio.io/redirection")
//                                                                (None when either is absent/empty)
datatype Pod = Pod(name: seq<CodePoint>, phase: Option<seq<CodePoint>>, redirection: Option<seq<CodePoint>>)

// "Running"
const Running: seq<CodePoint> := [82 as CodePoint, 117, 110, 110, 105, 110, 103]
// "enabled"
const Enabled: seq<CodePoint> := [101 as CodePoint, 110, 97, 98, 108, 101, 100]

// str(n) for a non-negative Python int: its decimal digits, no sign, no leading zeros.
function Digit(d: nat): CodePoint
  requires d < 10
{
  (48 + d) as CodePoint
}

function Decimal(n: nat): seq<CodePoint>
  decreases n
{
  if n < 10 then [Digit(n)] else Decimal(n / 10) + [Digit(n % 10)]
}

// ---------------------------------------------------------------------------------------------
// Transliteration of UNENROLLED_PY (mesh-check.sh 59–66). The result is the sequence of lines
// printed to stdout, each without its trailing newline.
// ---------------------------------------------------------------------------------------------

// line 61: [p for p in items if p["status"].get("phase") == "Running"]
//          (comprehension iterates items left to right, appending each match)
function Comprehension(items: seq<Pod>, i: nat, acc: seq<Pod>): seq<Pod>
  requires i <= |items|
  decreases |items| - i
{
  if i == |items| then acc
  else if items[i].phase == Some(Running) then Comprehension(items, i + 1, acc + [items[i]])
  else Comprehension(items, i + 1, acc)
}

// lines 63–65: for p in pods: if (annotations or {}).get(...) != "enabled": print(name)
function ForLoop(pods: seq<Pod>, i: nat, out: seq<seq<CodePoint>>): seq<seq<CodePoint>>
  requires i <= |pods|
  decreases |pods| - i
{
  if i == |pods| then out
  else if pods[i].redirection != Some(Enabled) then ForLoop(pods, i + 1, out + [pods[i].name])
  else ForLoop(pods, i + 1, out)
}

function Unenrolled(items: seq<Pod>): seq<seq<CodePoint>>
{
  var pods := Comprehension(items, 0, []);   // line 61
  var out := [Decimal(|pods|)];              // line 62: print(len(pods))
  ForLoop(pods, 0, out)                      // lines 63–65
}

// ---------------------------------------------------------------------------------------------
// Specification helpers (independent, structurally recursive)
// ---------------------------------------------------------------------------------------------

function RunningPods(pods: seq<Pod>): seq<Pod>
{
  if |pods| == 0 then []
  else (if pods[0].phase == Some(Running) then [pods[0]] else []) + RunningPods(pods[1..])
}

function UnenrolledNames(pods: seq<Pod>): seq<seq<CodePoint>>
{
  if |pods| == 0 then []
  else (if pods[0].redirection != Some(Enabled) then [pods[0].name] else []) + UnenrolledNames(pods[1..])
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------------------------

lemma ComprehensionIsFilter(items: seq<Pod>, i: nat, acc: seq<Pod>)
  requires i <= |items|
  ensures Comprehension(items, i, acc) == acc + RunningPods(items[i..])
  decreases |items| - i
{
  if i == |items| {
    assert items[i..] == [];
  } else {
    assert items[i..][1..] == items[i + 1..];
    ComprehensionIsFilter(items, i + 1, if items[i].phase == Some(Running) then acc + [items[i]] else acc);
  }
}

lemma ForLoopIsFilter(pods: seq<Pod>, i: nat, out: seq<seq<CodePoint>>)
  requires i <= |pods|
  ensures ForLoop(pods, i, out) == out + UnenrolledNames(pods[i..])
  decreases |pods| - i
{
  if i == |pods| {
    assert pods[i..] == [];
  } else {
    assert pods[i..][1..] == pods[i + 1..];
    ForLoopIsFilter(pods, i + 1, if pods[i].redirection != Some(Enabled) then out + [pods[i].name] else out);
  }
}

// ---------------------------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------------------------

// Citation: test "running pods counted and the unenrolled one named" (T25 S1), generalised; "every release pod enrolled" (§6 line 518)
lemma P1_RunningCountThenUnenrolled(pods: seq<Pod>)
  ensures Unenrolled(pods) == [Decimal(|RunningPods(pods)|)] + UnenrolledNames(RunningPods(pods))
{
  var running := Comprehension(pods, 0, []);
  ComprehensionIsFilter(pods, 0, []);
  assert pods[0..] == pods;
  assert running == RunningPods(pods);
  ForLoopIsFilter(running, 0, [Decimal(|running|)]);
  assert running[0..] == running;
}
