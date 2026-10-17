// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T1 S3
// Source: Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs `RejectCaseDuplicateProperties`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/SchemaRegistrationOrchestrator.RejectCaseDuplicateProperties.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary
// ---------------------------------------------------------------------------

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

datatype StatusCode = OK | Cancelled | Unknown | InvalidArgument | DeadlineExceeded | NotFound
  | AlreadyExists | PermissionDenied | ResourceExhausted | FailedPrecondition | Aborted
  | OutOfRange | Unimplemented | Internal | Unavailable | DataLoss | Unauthenticated

// Approved axiom: the ordinal upper-casing that StringComparison.OrdinalIgnoreCase compares under.
function OrdinalUpper(s: seq<char>): seq<char>

// StringComparer.OrdinalIgnoreCase.Equals
predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

// ---------------------------------------------------------------------------
// Candidate model
// ---------------------------------------------------------------------------

// PropertyDescriptor: only `name` is read. Proto3 strings are never null.
datatype Prop = Prop(Name: CsString)

// TypeDescriptor: `type_name` and `repeated properties` (the key is one of the properties).
datatype TypeDesc = TypeDesc(TypeName: CsString, Properties: seq<Prop>)

// void-or-throw: Pass is a normal return, Refused is `throw new RpcException(new Status(code, detail))`.
datatype Check = Pass | Refused(code: StatusCode, detail: seq<char>)

// ---- Enumerable.GroupBy(keySelector: p => p.Name, StringComparer.OrdinalIgnoreCase) ----
// A Lookup: groups in order of their key's first appearance; a group's Key is the key of its
// first element; each element joins the group whose stored key compares equal to its own key
// (comparer.Equals(g.Key, key)), or opens a new group at the end.
datatype Group = Group(Key: CsString, Elems: seq<Prop>)

// Index of the first group whose key compares equal to `name`, if any (Lookup.GetGrouping).
function FindGroup(gs: seq<Group>, name: seq<char>): Option<nat>
  ensures FindGroup(gs, name).Some? ==>
    FindGroup(gs, name).value < |gs| && EqIC(gs[FindGroup(gs, name).value].Key, name)
  ensures FindGroup(gs, name).None? ==> forall k :: 0 <= k < |gs| ==> !EqIC(gs[k].Key, name)
  decreases |gs|
{
  if |gs| == 0 then None
  else if EqIC(gs[0].Key, name) then Some(0)
  else match FindGroup(gs[1..], name)
    case None => None
    case Some(k) => Some(k + 1)
}

// Lookup.GetGrouping(key, create: true) followed by Grouping.Add(element).
function AddTo(gs: seq<Group>, p: Prop): seq<Group>
{
  match FindGroup(gs, p.Name)
    case Some(k) => gs[k := Group(gs[k].Key, gs[k].Elems + [p])]
    case None => gs + [Group(p.Name, [p])]
}

// Lookup.Create: elements are added in source order.
function GroupByName(ps: seq<Prop>): seq<Group>
  decreases |ps|
{
  if |ps| == 0 then [] else AddTo(GroupByName(ps[..|ps| - 1]), ps[|ps| - 1])
}

// .FirstOrDefault(g => g.Count() > 1)
function FirstOrDefaultDup(gs: seq<Group>): Option<Group>
  decreases |gs|
{
  if |gs| == 0 then None
  else if |gs[0].Elems| > 1 then Some(gs[0])
  else FirstOrDefaultDup(gs[1..])
}

// string.Join(", ", xs)
function Join(sep: seq<char>, xs: seq<seq<char>>): seq<char>
  decreases |xs|
{
  if |xs| == 0 then [] else if |xs| == 1 then xs[0] else xs[0] + sep + Join(sep, xs[1..])
}

// duplicate.Select(p => $"'{p.Name}'")
function QuotedNames(ps: seq<Prop>): seq<seq<char>>
  decreases |ps|
{
  if |ps| == 0 then [] else ["'" + ps[0].Name + "'"] + QuotedNames(ps[1..])
}

function RejectCaseDuplicateProperties(td: TypeDesc): Check
{
  var duplicate := FirstOrDefaultDup(GroupByName(td.Properties));
  if duplicate.None? then Pass
  else
    Refused(InvalidArgument,
      "'" + td.TypeName + "' declares properties " + Join(", ", QuotedNames(duplicate.value.Elems)) + " " +
      "whose names are equal ignoring letter case. Property names must be distinct ignoring case; " +
      "rename one of them.")
}

// ---------------------------------------------------------------------------
// Proof helpers
// ---------------------------------------------------------------------------

// The elements of ps whose name compares equal to key, in order: what a Lookup group holds.
function Filter(ps: seq<Prop>, key: seq<char>): seq<Prop>
  decreases |ps|
{
  if |ps| == 0 then []
  else Filter(ps[..|ps| - 1], key) + (if EqIC(ps[|ps| - 1].Name, key) then [ps[|ps| - 1]] else [])
}

// The GroupBy invariant: keys pairwise distinct ignoring case, each group holds exactly the
// matching elements, and every element is covered by some group.
ghost predicate GroupInv(ps: seq<Prop>, gs: seq<Group>)
{
  (forall a, b :: 0 <= a < b < |gs| ==> !EqIC(gs[a].Key, gs[b].Key))
  && (forall k :: 0 <= k < |gs| ==> gs[k].Elems == Filter(ps, gs[k].Key))
  && (forall i :: 0 <= i < |ps| ==> exists k :: 0 <= k < |gs| && EqIC(gs[k].Key, ps[i].Name))
}

lemma FilterSnoc(ps: seq<Prop>, p: Prop, key: seq<char>)
  ensures Filter(ps + [p], key) == Filter(ps, key) + (if EqIC(p.Name, key) then [p] else [])
{
  assert (ps + [p])[..|ps + [p]| - 1] == ps;
}

lemma FilterEmptyNoMatch(ps: seq<Prop>, key: seq<char>, i: int)
  requires 0 <= i < |ps| && EqIC(ps[i].Name, key)
  ensures |Filter(ps, key)| >= 1
  decreases |ps|
{
  if i < |ps| - 1 {
    FilterEmptyNoMatch(ps[..|ps| - 1], key, i);
  }
}

lemma FilterNonEmptyHasMatch(ps: seq<Prop>, key: seq<char>)
  requires |Filter(ps, key)| >= 1
  ensures exists i :: 0 <= i < |ps| && EqIC(ps[i].Name, key)
  decreases |ps|
{
  var n := |ps|;
  if !EqIC(ps[n - 1].Name, key) {
    FilterNonEmptyHasMatch(ps[..n - 1], key);
    var i :| 0 <= i < n - 1 && EqIC(ps[..n - 1][i].Name, key);
    assert ps[i] == ps[..n - 1][i];
  }
}

lemma FilterNoMatch(ps: seq<Prop>, key: seq<char>)
  requires forall i :: 0 <= i < |ps| ==> !EqIC(ps[i].Name, key)
  ensures Filter(ps, key) == []
  decreases |ps|
{
  if |ps| > 0 {
    FilterNoMatch(ps[..|ps| - 1], key);
  }
}

lemma FilterTwoIff(ps: seq<Prop>, key: seq<char>)
  ensures |Filter(ps, key)| >= 2 <==>
    exists i, j :: 0 <= i < j < |ps| && EqIC(ps[i].Name, key) && EqIC(ps[j].Name, key)
  decreases |ps|
{
  if |ps| == 0 { return; }
  var n := |ps|;
  var init := ps[..n - 1];
  FilterTwoIff(init, key);
  if |Filter(ps, key)| >= 2 {
    if EqIC(ps[n - 1].Name, key) {
      FilterNonEmptyHasMatch(init, key);
      var i :| 0 <= i < |init| && EqIC(init[i].Name, key);
      assert ps[i] == init[i];
      assert 0 <= i < n - 1 < |ps| && EqIC(ps[i].Name, key) && EqIC(ps[n - 1].Name, key);
    } else {
      var i, j :| 0 <= i < j < |init| && EqIC(init[i].Name, key) && EqIC(init[j].Name, key);
      assert ps[i] == init[i] && ps[j] == init[j];
    }
  }
  if exists i, j :: 0 <= i < j < |ps| && EqIC(ps[i].Name, key) && EqIC(ps[j].Name, key) {
    var i, j :| 0 <= i < j < |ps| && EqIC(ps[i].Name, key) && EqIC(ps[j].Name, key);
    assert ps[i] == init[i];
    if j < n - 1 {
      assert ps[j] == init[j];
    } else {
      FilterEmptyNoMatch(init, key, i);
    }
  }
}

lemma AddToInv(ps: seq<Prop>, gs: seq<Group>, p: Prop)
  requires GroupInv(ps, gs)
  ensures GroupInv(ps + [p], AddTo(gs, p))
{
  var ps' := ps + [p];
  forall key: seq<char> ensures Filter(ps', key) == Filter(ps, key) + (if EqIC(p.Name, key) then [p] else []) {
    FilterSnoc(ps, p, key);
  }
  assert forall i :: 0 <= i < |ps| ==> ps'[i] == ps[i];
  match FindGroup(gs, p.Name)
  case Some(k) => {
    var gs' := gs[k := Group(gs[k].Key, gs[k].Elems + [p])];
    assert AddTo(gs, p) == gs';
    // p matches only group k: another match would make two keys equal ignoring case.
    forall k' | 0 <= k' < |gs'| ensures gs'[k'].Elems == Filter(ps', gs'[k'].Key) {
      if k' != k {
        assert !EqIC(gs[k'].Key, gs[k].Key);
      }
    }
    forall i | 0 <= i < |ps'| ensures exists k0 :: 0 <= k0 < |gs'| && EqIC(gs'[k0].Key, ps'[i].Name) {
      if i < |ps| {
        var k0 :| 0 <= k0 < |gs| && EqIC(gs[k0].Key, ps[i].Name);
        assert gs'[k0].Key == gs[k0].Key;
      } else {
        assert gs'[k].Key == gs[k].Key;
      }
    }
  }
  case None => {
    var gs' := gs + [Group(p.Name, [p])];
    assert AddTo(gs, p) == gs';
    // No earlier element matches p.Name: it would be covered by a group whose key matches p.Name.
    forall i | 0 <= i < |ps| ensures !EqIC(ps[i].Name, p.Name) {
      var k0 :| 0 <= k0 < |gs| && EqIC(gs[k0].Key, ps[i].Name);
    }
    FilterNoMatch(ps, p.Name);
    assert Filter(ps', p.Name) == [p];
    forall k' | 0 <= k' < |gs'| ensures gs'[k'].Elems == Filter(ps', gs'[k'].Key) {
      if k' < |gs| {
        assert gs'[k'] == gs[k'];
      }
    }
    forall i | 0 <= i < |ps'| ensures exists k0 :: 0 <= k0 < |gs'| && EqIC(gs'[k0].Key, ps'[i].Name) {
      if i < |ps| {
        var k0 :| 0 <= k0 < |gs| && EqIC(gs[k0].Key, ps[i].Name);
        assert gs'[k0] == gs[k0];
      } else {
        assert gs'[|gs|].Key == p.Name;
      }
    }
  }
}

lemma GroupByInv(ps: seq<Prop>)
  ensures GroupInv(ps, GroupByName(ps))
  decreases |ps|
{
  if |ps| == 0 { return; }
  var init := ps[..|ps| - 1];
  GroupByInv(init);
  AddToInv(init, GroupByName(init), ps[|ps| - 1]);
  assert init + [ps[|ps| - 1]] == ps;
}

lemma FirstOrDefaultDupSome(gs: seq<Group>)
  ensures FirstOrDefaultDup(gs).Some? <==> exists k :: 0 <= k < |gs| && |gs[k].Elems| > 1
  decreases |gs|
{
  if |gs| == 0 { return; }
  FirstOrDefaultDupSome(gs[1..]);
  if exists k :: 0 <= k < |gs| && |gs[k].Elems| > 1 {
    var k :| 0 <= k < |gs| && |gs[k].Elems| > 1;
    if k > 0 { assert gs[1..][k - 1] == gs[k]; }
  }
  if FirstOrDefaultDup(gs).Some? && |gs[0].Elems| <= 1 {
    var k :| 0 <= k < |gs[1..]| && |gs[1..][k].Elems| > 1;
    assert gs[k + 1] == gs[1..][k];
  }
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "`RegisterSchema` refuses with `InvalidArgument`:" / "two properties, or the key and a property, whose names differ only by case;" (spec §2.4)
lemma P1_PropertiesEqualIgnoringCaseAreRefused(td: TypeDesc)
  ensures RejectCaseDuplicateProperties(td).Refused? <==>
    exists i, j :: 0 <= i < j < |td.Properties| && EqIC(td.Properties[i].Name, td.Properties[j].Name)
  ensures RejectCaseDuplicateProperties(td).Refused? ==> RejectCaseDuplicateProperties(td).code == InvalidArgument
{
  var ps := td.Properties;
  var gs := GroupByName(ps);
  GroupByInv(ps);
  FirstOrDefaultDupSome(gs);
  if RejectCaseDuplicateProperties(td).Refused? {
    var k :| 0 <= k < |gs| && |gs[k].Elems| > 1;
    FilterTwoIff(ps, gs[k].Key);
    var i, j :| 0 <= i < j < |ps| && EqIC(ps[i].Name, gs[k].Key) && EqIC(ps[j].Name, gs[k].Key);
    assert EqIC(ps[i].Name, ps[j].Name);
  }
  if exists i, j :: 0 <= i < j < |ps| && EqIC(ps[i].Name, ps[j].Name) {
    var i, j :| 0 <= i < j < |ps| && EqIC(ps[i].Name, ps[j].Name);
    var k :| 0 <= k < |gs| && EqIC(gs[k].Key, ps[i].Name);
    FilterTwoIff(ps, gs[k].Key);
    assert EqIC(ps[i].Name, gs[k].Key) && EqIC(ps[j].Name, gs[k].Key);
    assert |gs[k].Elems| > 1;
  }
}
