// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T11 S1
// Source: ci/pins.py freshness (with norm)
// Replay base: 680eb9cf5a6dc2d3dd6374446822969441bfed0f
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/Pins.Freshness.dfy

// ---- strings and library boundary ----------------------------------------------------------------------

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

// Row as in Pins.Consistency (load_table's row dict; `note` is never read by freshness).
datatype Location = Location(file: Str, regex: Str)
datatype Row = Row(name: Str, source: Str, locations: seq<Location>)

// The `gh issue create/edit/close` calls (ci/pins.py:251, :253, :257).
datatype Action = Create(title: Str) | Edit(number: int) | Close(number: int)

// "dependabot" (ci/pins.py:231)
const KindDependabot: Str := [100, 101, 112, 101, 110, 100, 97, 98, 111, 116]
// "eks-addon" (ci/pins.py:233)
const KindEks: Str := [101, 107, 115, 45, 97, 100, 100, 111, 110]
// ISSUE_PREFIX = "Pin behind: " (ci/pins.py:36)
const IssuePrefix: Str := [80, 105, 110, 32, 98, 101, 104, 105, 110, 100, 58, 32]
// "v" (ci/pins.py:90)
const LowerV: CodePoint := 118

// Approved axioms (uninterpreted, no contract).
// `row["source"].split()[0]` (ci/pins.py:230)
function Kind(source: Str): Str

// `found(row, root)[0][1]`; None when it raises PinError/OSError (ci/pins.py:237)
function FoundFirst(row: Row): Option<Str>

// `newest(row["source"], root, http=http, run=run)`; None for the caught set (ci/pins.py:238)
function NewestOf(source: Str): Option<Str>

// `open_issue(row["name"], run)` (ci/pins.py:243)
function OpenIssue(name: Str): Option<int>

// ---- transliteration -----------------------------------------------------------------------------------

// ci/pins.py:89-90
function Norm(version: Str): Str
{
  if |version| > 0 && version[0] == LowerV then version[1..] else version
}

// ci/pins.py:244 (and open_issue's title, :218)
function Title(name: Str): Str
{
  IssuePrefix + name
}

// One iteration of the loop body, ci/pins.py:230-258: the gh actions it takes, and whether it counted an error.
function RowStep(row: Row, role: bool): (seq<Action>, bool)
{
  var kind := Kind(row.source);
  if kind == KindDependabot then
    ([], false)                                            // continue
  else if kind == KindEks && !role then
    ([], false)                                            // print skipped; continue
  else
    var pinned := FoundFirst(row);
    if pinned.None? then
      ([], true)                                           // except: errors += 1; continue
    else
      var latest := NewestOf(row.source);
      if latest.None? then
        ([], true)                                         // except: errors += 1; continue
      else
        var number := OpenIssue(row.name);
        var title := Title(row.name);
        if Norm(pinned.value) != Norm(latest.value) then
          if number.None? then
            ([Create(title)], false)
          else
            ([Edit(number.value)], false)
        else
          if number.Some? then
            ([Close(number.value)], false)
          else
            ([], false)
}

// The `for row in rows` loop (ci/pins.py:228-259) with its accumulators: the gh calls made so far and `errors`.
function FreshnessLoop(rows: seq<Row>, role: bool, actions: seq<Action>, errors: int): (seq<Action>, int)
  decreases |rows|
{
  if rows == [] then
    (actions, errors)
  else
    var step := RowStep(rows[0], role);
    FreshnessLoop(rows[1..], role, actions + step.0, errors + (if step.1 then 1 else 0))
}

// ci/pins.py:225-259: freshness(rows, ...) -> (the gh calls in order, the returned error count).
function Freshness(rows: seq<Row>, role: bool): (seq<Action>, int)
{
  FreshnessLoop(rows, role, [], 0)
}

// ---- helper lemmas -------------------------------------------------------------------------------------

lemma LoopAccumulates(rows: seq<Row>, role: bool, actions: seq<Action>, errors: int)
  ensures FreshnessLoop(rows, role, actions, errors)
       == (actions + FreshnessLoop(rows, role, [], 0).0, errors + FreshnessLoop(rows, role, [], 0).1)
  decreases |rows|
{
  if rows != [] {
    var step := RowStep(rows[0], role);
    var inc := if step.1 then 1 else 0;
    LoopAccumulates(rows[1..], role, actions + step.0, errors + inc);
    LoopAccumulates(rows[1..], role, [] + step.0, 0 + inc);
    assert [] + step.0 == step.0;
    assert (actions + step.0) + FreshnessLoop(rows[1..], role, [], 0).0
        == actions + (step.0 + FreshnessLoop(rows[1..], role, [], 0).0);
  } else {
    assert actions + [] == actions;
  }
}

// ---- properties ----------------------------------------------------------------------------------------

// Citation: design PinChecks.P2_DependabotRowsUntouched: "for each non-`dependabot` row" (spec line 157); test `test_dependabot_rows_never_reach_a_source_or_an_issue`.
lemma P1_DependabotRowsUntouched(row: Row, role: bool)
  requires Kind(row.source) == KindDependabot
  ensures RowStep(row, role) == ([], false)
{
}

// Citation: design PinChecks.P3_IssueOpenIffStale: "opens or updates one issue `Pin behind: <name>` per stale row, closes it when current" (spec line 157); tests `test_a_stale_row_without_an_issue_opens_one`, `test_a_stale_row_with_an_open_issue_updates_it`.
lemma P2_StaleRowOpensOrUpdatesItsIssue(row: Row, role: bool)
  requires Kind(row.source) != KindDependabot && (Kind(row.source) != KindEks || role)
  requires FoundFirst(row).Some? && NewestOf(row.source).Some? && Norm(FoundFirst(row).value) != Norm(NewestOf(row.source).value)
  ensures RowStep(row, role) == (if OpenIssue(row.name).None? then [Create(Title(row.name))] else [Edit(OpenIssue(row.name).value)], false)
{
}

// Citation: same (spec line 157); tests `test_a_current_row_closes_its_open_issue`, `test_a_current_row_with_no_issue_touches_nothing`.
lemma P3_CurrentRowClosesItsIssue(row: Row, role: bool)
  requires Kind(row.source) != KindDependabot && (Kind(row.source) != KindEks || role)
  requires FoundFirst(row).Some? && NewestOf(row.source).Some? && Norm(FoundFirst(row).value) == Norm(NewestOf(row.source).value)
  ensures RowStep(row, role) == (if OpenIssue(row.name).Some? then [Close(OpenIssue(row.name).value)] else [], false)
{
}

// Citation: design PinChecks.P5_NoRoleLeavesEksUntouched: "unset → the EKS rows report \"skipped: no AWS role\"" (spec line 151); test `test_without_the_aws_role_the_eks_rows_are_skipped_and_the_rest_run`.
lemma P4_NoRoleLeavesEksUntouched(row: Row)
  requires Kind(row.source) == KindEks
  ensures RowStep(row, false) == ([], false)
{
}

// Citation: design PinChecks.P6_NoRoleStillChecksOthers: "and the rest run." (spec line 151); same test.
lemma P5_NoRoleStillChecksOthers(row: Row)
  requires Kind(row.source) != KindEks
  ensures RowStep(row, false) == RowStep(row, true)
{
}

// Citation: "opens or updates one issue `Pin behind: <name>` per stale row" (spec line 157); "`freshness` opens, edits or closes one `Pin behind: <name>` issue per row through `gh`" (plan T11 S1).
lemma P6_RowsInTableOrder(rows: seq<Row>, role: bool)
  ensures Freshness(rows, role) == if rows == [] then ([], 0) else (RowStep(rows[0], role).0 + Freshness(rows[1..], role).0, (if RowStep(rows[0], role).1 then 1 else 0) + Freshness(rows[1..], role).1)
{
  if rows != [] {
    var step := RowStep(rows[0], role);
    var inc := if step.1 then 1 else 0;
    assert [] + step.0 == step.0;
    LoopAccumulates(rows[1..], role, step.0, inc);
  }
}
