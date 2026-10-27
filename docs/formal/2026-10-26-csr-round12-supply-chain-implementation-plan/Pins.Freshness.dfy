// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T11 S1
// Source: ci/pins.py freshness (with norm)
// Replay base: a6c008dce9e4c846f8c34456ddd6297636d31979
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/Pins.Freshness.dfy

// ---- strings and helpers -------------------------------------------------------------------------------

newtype CodePoint = x: int | 0 <= x < 0x110000
type Str = seq<CodePoint>

datatype Option<T> = None | Some(value: T)

// ci/pins.py:36  ISSUE_PREFIX = "Pin behind: "
const IssuePrefix: Str := [80, 105, 110, 32, 98, 101, 104, 105, 110, 100, 58, 32]
// "dependabot"
const KindDependabot: Str := [100, 101, 112, 101, 110, 100, 97, 98, 111, 116]
// "eks-addon"
const KindEks: Str := [101, 107, 115, 45, 97, 100, 100, 111, 110]
// "v"
const LowerV: CodePoint := 118

// A row of ci/pins.yaml as load_table returns it (source and locations are present: load_table enforces both).
datatype Location = Location(file: Str, regex: Str)
datatype Row = Row(name: Str, source: Str, locations: seq<Location>)

// The gh issue create/edit/close calls.
datatype Action = Create(title: Str) | Edit(number: int) | Close(number: int)

// ---- approved axioms (library boundary) ----------------------------------------------------------------

// source.split()[0]
function Kind(source: Str): Str

// found(row, root)[0][1]; None when found raises PinError/OSError
function FoundFirst(row: Row): Option<Str>

// newest(source, root, http=http, run=run); None for the caught set
function NewestOf(source: Str): Option<Str>

// open_issue(name, run)
function OpenIssue(name: Str): Option<int>

// ---- transliteration -----------------------------------------------------------------------------------

// ci/pins.py:89-90  norm
function Norm(version: Str): Str
{
  if |version| > 0 && version[0] == LowerV then version[1..] else version
}

// ci/pins.py:244  title = ISSUE_PREFIX + row["name"]  (also open_issue's title, :218)
function Title(name: Str): Str
{
  IssuePrefix + name
}

// ci/pins.py:230-258  one iteration of the loop body: the gh actions it takes, and whether it counts an error.
function RowStep(row: Row, role: bool): (seq<Action>, bool)
{
  var kind := Kind(row.source);                                  // :230
  if kind == KindDependabot then ([], false)                     // :231-232 continue
  else if kind == KindEks && !role then ([], false)              // :233-235 print skipped; continue
  else
    var pinned := FoundFirst(row);                               // :237 (inside try)
    if pinned.None? then ([], true)                              // :239-242 errors += 1; continue
    else
      var latest := NewestOf(row.source);                        // :238 (inside try)
      if latest.None? then ([], true)                            // :239-242 errors += 1; continue
      else
        var number := OpenIssue(row.name);                       // :243
        var title := Title(row.name);                            // :244
        if Norm(pinned.value) != Norm(latest.value) then         // :245
          if number.None? then ([Create(title)], false)          // :250-251
          else ([Edit(number.value)], false)                     // :252-253
        else
          if number.Some? then ([Close(number.value)], false)    // :256-257
          else ([], false)
}

// ci/pins.py:228-259  the for loop, with errors and the emitted actions as accumulators.
function FreshnessLoop(rows: seq<Row>, role: bool, acts: seq<Action>, errors: int): (seq<Action>, int)
  decreases |rows|
{
  if rows == [] then (acts, errors)                              // :259 return errors
  else
    var step := RowStep(rows[0], role);
    FreshnessLoop(rows[1..], role, acts + step.0, errors + (if step.1 then 1 else 0))
}

// ci/pins.py:225  freshness(rows, root, http, run, aws_role); role is the truthiness of aws_role.
function Freshness(rows: seq<Row>, role: bool): (seq<Action>, int)
{
  FreshnessLoop(rows, role, [], 0)                               // :228 errors = 0
}

// ---- helper lemmas -------------------------------------------------------------------------------------

lemma LoopAccumulates(rows: seq<Row>, role: bool, acts: seq<Action>, errors: int)
  ensures FreshnessLoop(rows, role, acts, errors) == (acts + Freshness(rows, role).0, errors + Freshness(rows, role).1)
  decreases |rows|
{
  if rows == [] {
    assert acts + [] == acts;
  } else {
    var step := RowStep(rows[0], role);
    var e := if step.1 then 1 else 0;
    LoopAccumulates(rows[1..], role, acts + step.0, errors + e);
    LoopAccumulates(rows[1..], role, [] + step.0, 0 + e);
    assert [] + step.0 == step.0;
    assert (acts + step.0) + Freshness(rows[1..], role).0 == acts + (step.0 + Freshness(rows[1..], role).0);
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
    var e := if step.1 then 1 else 0;
    LoopAccumulates(rows[1..], role, [] + step.0, 0 + e);
    assert [] + step.0 == step.0;
  }
}
