# Axis-Completeness Check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-17-axis-completeness-check-design.md` (commit SHA: `dfd9c9ea9735518fb532bd289f85bac25db99337`)

**Goal:** Add one coverage-gate check that fails when an authored axis's stated coverage areas and its requirements do not account for each other.

**Architecture:** Each authored axis in `docs/standards/iverson-client-standard.md` gains a `#### Coverage` table of `| Area | Status | Evidence |`. A new fixture-only parser in the test project extracts those tables, attributing each to the `### AXIS — Name` heading it sits under. A new check in `RequirementsCoverageGateTests` binds areas and requirements in both directions.

**Tech stack:** C# / xUnit / FluentAssertions, in `Iverson.Server/Iverson.ClientConformance.Tests`. No new dependency.

---

## File Structure

**Create**
- `Iverson.Server/Iverson.ClientConformance.Tests/CoverageTableParser.cs` — parses `| Area | Status | Evidence |` tables, tracking the enclosing axis heading
- `Iverson.Server/Iverson.ClientConformance.Tests/CoverageTableParserTests.cs` — fixture-only unit tests for that parser

**Modify**
- `docs/standards/iverson-client-standard.md` — four `#### Coverage` tables; REG's three deferral bullets become rows
- `Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs` — the new check, added as a method on the existing class

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time; NOT re-verified here:

- **A1** `RequirementTableParser` lives in the test project and is the only parser the gate uses — `RequirementTableParser.cs:20`
- **A2** A coverage row does **not** reach `MalformedLines`: the parser opens a table only on the exact requirement header (`:37`), closes on the first non-pipe line (`:50-55`), and skips lines outside an open table (`:43-46`). The change is additive
- **A3** Check 3 fails when `MalformedLines` is non-empty — `RequirementsCoverageGateTests.cs:145-153`
- **A4** The parser does not track axis headings
- **A5** IDs encode their axis; `IdShapePattern` and `KnownAxes` exist — `RequirementsCoverageGateTests.cs:29-31`
- **A6** Check 1 filters on `Status == "Active"` — `RequirementsCoverageGateTests.cs:104-107`
- **A7** All nine axis sections use `### AXIS — Name`
- **A8** Four axes carry rows (DECL, REL, REG, LIFE); five carry headers with none
- **A9** REG's three deferred areas exist as prose — `iverson-client-standard.md:187-210`
- **A10** Four `####` subsections already exist; `#### Coverage` does not collide
- **A11** Nothing outside the gate tests parses the standard
- **A12** Adding a check to `RequirementsCoverageGateTests.cs` is the established pattern
- **A13** `Requirements.cs` consts map 1:1 onto Active IDs
- **A14** No existing table could be misparsed as a coverage table
- **A15** No non-axis `###` heading can be read as an axis heading

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `CoverageTableParser.cs` does not yet exist (Task 2 creates it) | `find . -name "CoverageTableParser*.cs"` → 0 results |
| P2 | File path | `CoverageTableParserTests.cs` does not yet exist | same `find` → 0 results |
| P3 | File path | `RequirementTableParserTests.cs` exists and is fixture-only — the pattern Task 2 mirrors | 148 lines; 0 occurrences of `StandardPath` or `File.ReadAllText` |
| P4 | Command | The test project is `Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj` | file exists at that path |
| P5 | Signature | `RequirementTableParser.Parse` returns `Result(List<(string Id, string Status)> Rows, List<string> MalformedLines)` | `RequirementTableParser.cs:25,27` |
| P6 | Signature | The gate's helpers are `private static` on `RequirementsCoverageGateTests`, so the new check must be a method **on that class** — a separate class could not reach them | `IdShapePattern:29`, `KnownAxes:31`, `StandardPath():53`, `ParseDeclaredRequirements():66`, all `private static` |
| P7 | Ordering | Task 1 depends on nothing Task 2 creates (markdown only), so the suite stays green between tasks | Task 1 touches only `iverson-client-standard.md`; only the gate test reads it |
| P8 | Consumer impact | Adding `#### Coverage` tables breaks no existing check | only `RequirementsCoverageGateTests.cs` reads the standard, and per A2 its parser cannot see coverage rows |
| P9 | Code validity | The foreign-axis-ID mutation is constructible — ≥2 axes carry Active rows | DECL, LIFE, REG, REL all have Active rows |
| P10 | File path | REG's deferral bullets are still present to be replaced | `iverson-client-standard.md:187` `#### Deferred coverage (non-normative)` |
| P11 | Code validity | The new parser must be `internal static class` in `namespace Iverson.ClientConformance.Tests` to be visible to the gate test | `RequirementTableParser.cs:1,20` |
| P12 | Consumer impact | No test asserts on the standard's length or section positions, so insertion is safe | only the gate test reads it; its sole content assertion is over `MalformedLines` (`:148`) |
| P13 | Command | Commit messages are imperative lowercase with no prefix | `git log --oneline -10`: "author the REG axis…", "split IVC-LIFE-005…" |
| P14 | Sibling sweep | Every identifier the tasks name resolves at its point of use (meta-class: every referenced name resolves) | `RequirementTableParser:20`, `Result:25`, `MalformedLines:25`, `StandardPath:53`, `ParseDeclaredRequirements:66`, `KnownAxes:31`, `IdShapePattern:29`; `CoverageTableParser`/`Tests` are new per P1/P2 |

## Tasks

### Task 1: Author the four coverage ledgers

Ledgers land before the check. If the check landed first, failure mode 1 would fire for all four authored axes and the suite would be red at a task boundary.

**Files:**
- Modify: `docs/standards/iverson-client-standard.md`

**Interfaces:**
- Produces: the four `#### Coverage` tables Task 2's check parses.

- [ ] **Step 1: Author `REG`'s ledger.** Largely transcription. Its three deferred areas already exist as prose at `:187-210` — reregistration, authorization rules at registration time, schema drift — each with a reason. Convert them to `Deferred` rows carrying those reasons, and add `Covered` rows for the two live rules (`IVC-REG-002`, `IVC-REG-003`). Replace the three bullets; **keep** the closing paragraph stating that descriptor contents are not part of the deferral — it is an anti-claim about another axis's territory, not an area of REG's own. `IVC-REG-001` is `Retired` and must not appear in any Evidence cell.

- [ ] **Step 2: Author `DECL`, `REL` and `LIFE` ledgers.** Genuine design work, not transcription. Every `Active` requirement in each axis must be claimed by at least one area, and every `Covered` area must cite Active IDs from its own axis only. The `Retired` rows (`IVC-REL-009`, `IVC-LIFE-005`) take no area. For `LIFE`, `IVC-LIFE-007` is `Active` and failing live for four clients — it is `Covered` (a requirement exists and is asserted); its live failure is recorded separately under the existing `#### Known non-conformance` section, which this task does not touch.

- [ ] **Step 3: Confirm the existing suite is unaffected.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
```
Per P8 this must pass unchanged — the markdown edit is invisible to every existing check. A failure here means A2 is wrong and Task 2's design needs revisiting before proceeding.

- [ ] **Step 4: Commit.**
```bash
git add docs/standards/iverson-client-standard.md
git commit -m "author coverage ledgers for the four authored axes"
```

### Task 2: The coverage parser and the gate check

**Files:**
- Create: `Iverson.Server/Iverson.ClientConformance.Tests/CoverageTableParser.cs`
- Create: `Iverson.Server/Iverson.ClientConformance.Tests/CoverageTableParserTests.cs`
- Modify: `Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs`

**Interfaces:**
- Consumes: Task 1's four ledgers; `RequirementTableParser.Parse(...).Rows` for the Active requirement set.

- [ ] **Step 1: Write the parser's failing tests first**, in `CoverageTableParserTests.cs`, fixture-only — inline markdown strings, never the real standard (P3's pattern). Cover: a well-formed table under an axis heading; two tables under different headings attributed correctly; a `|`-leading row inside an open table that does not parse, landing in a malformed collection rather than closing the table; a blank line closing a table; a coverage table under a non-axis `###` heading attributed to no axis (per A15); and a requirement table not being parsed as a coverage table.

- [ ] **Step 2: Implement `CoverageTableParser`.** `internal static class` in `namespace Iverson.ClientConformance.Tests` (P11). It opens a table on the exact header `| Area | Status | Evidence |`, closes on the first line inside the table that does not start with `|`, and — mirroring the hardened requirement parser — records an unparsable `|`-leading row as malformed and **continues**, so one bad row cannot make the rest of the table vanish. It tracks the current `### AXIS — Name` heading, matching only headings whose axis token is in the known set.

- [ ] **Step 3: Add the check to `RequirementsCoverageGateTests`**, as a method on that class so it can reach the existing `private static` helpers (P6). Implement all six failure modes from the spec, each message naming the offending axis, area or ID:
  1. an axis with ≥1 `Active` requirement and no `#### Coverage` table
  2. a `Status` cell that is neither `Covered` nor `Deferred`
  3. a `Covered` area citing no ID, a nonexistent ID, a `Retired` ID, or an ID from another axis
  4. a `Deferred` area with an empty reason
  5. an `Active` requirement claimed by no area
  6. a malformed coverage row

  The Active requirement set comes from `ParseDeclaredRequirements(markdown)` filtered to `Active` (P5, A6); each requirement's axis is derived from its ID via `IdShapePattern` (A5).

- [ ] **Step 4: Prove the check can fail.** Three mutations, each applied to the standard, run, then reverted:
  - delete an authored axis's `#### Coverage` table → must fail naming that axis (mode 1)
  - delete one area row while leaving its requirements → must fail naming the orphaned requirement (mode 5)
  - change a `Covered` area's evidence to an ID from another axis → must fail naming that ID (mode 3)

  Record the actual failure output for each. A mutation that reddens nothing means the check does not bind.

- [ ] **Step 5: Run the suite and commit.**
```bash
dotnet test Iverson.Server/Iverson.ClientConformance.Tests/Iverson.ClientConformance.Tests.csproj
git add Iverson.Server/Iverson.ClientConformance.Tests
git commit -m "add the coverage gate's axis-completeness check"
```

## Known issues inherited from spec

- The check cannot detect an omitted area (see *What this check does not do*). Accepted: no
  mechanical check can know an axis's true area set, and inventing one would require a second
  authority the document does not have.
- `DECL` and `LIFE` also lack the backstop assertion the standard's authoring notes make binding
  for every axis (`iverson-client-standard.md:143-150`). That gap predates this design and is not
  addressed here.
