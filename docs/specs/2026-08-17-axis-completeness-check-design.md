# The coverage gate's axis-completeness check

## Problem

The coverage gate binds requirements to executable evidence in both directions: every `Active`
requirement ID in the standard has a const in `Requirements.cs`, every const has a citing
assertion, and neither set may contain an ID the other lacks. What it cannot see is an axis whose
*claimed* coverage exceeds what its requirements deliver.

`REG` is the worked instance. The nine-axes table describes it as "Schema registration and
reregistration behaviour". `Reregistrar.cs` exercises reregistration on every conformance run, and
no assertion cites a requirement ID against that behaviour. The axis had two `Active`
requirements, every gate check was green, and the divergence between what the axis said it covered
and what it actually verified was found by a human reviewer reading prose — not by the gate.

The gap generalizes: an under-authored axis stays green forever. Six axes remain to be authored,
so the failure has six more chances to recur, each cheaper to prevent than to audit afterward.

## What this document is

The design for one new gate check and the document convention it enforces. It does not author any
requirement, and it does not change what any existing check does.

## Decisions taken

1. **The check enforces claimed-coverage binding, not axis non-emptiness.** A cheaper check —
   "every axis has at least one `Active` requirement" — would have passed `REG` and would stop
   firing entirely once the remaining axes are authored. It enforces a property that is not the
   one that broke.

2. **The ledger lives in the standard, as a table.** The document is already the source of truth
   that `Requirements.cs` mirrors; a code-side coverage registry would invert that direction and
   create a second artifact with nothing binding it to the first. A table also reuses the existing
   pipe-table conventions and sits adjacent to the requirements it describes.

3. **An axis is exempt only while it has zero requirement rows.** The exemption is narrow and
   self-closing: an empty axis is already visible to any reader, and the ledger becomes mandatory
   at the moment its first requirement lands — which is when the author has the knowledge to write
   it truthfully. The consequence is that this check cannot fire until an axis is authored.

4. **Binding is bidirectional**, matching check 1's existing discipline. A `Covered` area must cite
   at least one existing `Active` requirement from its own axis; every `Active` requirement in the
   axis must be claimed by at least one area. The reverse direction catches the mirror defect — a
   requirement that sits outside every stated area.

5. **The change is additive.** The existing `RequirementTableParser` is not modified (see
   *Verified assumptions*, A2).

## The coverage table

Each authored axis carries a `#### Coverage` subsection immediately below its requirement table:

```markdown
#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Foreign-key synthesis | Covered | IVC-REL-001, IVC-REL-004 |
| Navigation-property naming | Covered | IVC-REL-003 |
| Reregistration | Deferred | Exercised by Reregistrar.cs on every run but not asserted as a normative claim |
```

- **Area** — free text. Its identity is the string; there is no area registry.
- **Status** — exactly `Covered` or `Deferred`.
- **Evidence** — when `Covered`, a comma-separated list of requirement IDs; when `Deferred`, a
  non-empty prose reason.

Rules that follow from the existing conventions rather than from new choices:

- **`Retired` requirements are excluded from reverse binding**, matching check 1, which filters to
  `Active` before comparing against consts. Requiring an area to claim a retired row would force
  ledgers to carry dead entries permanently.
- **An area cites requirements only from its own axis.** Cross-axis citation would let one axis
  discharge an area by pointing at another's requirements — the confusion `REG`'s existing prose
  needed a paragraph to prevent.

## The check

One new test in `RequirementsCoverageGateTests.cs`, fed by a new coverage-table parser that tracks
the current `### AXIS — Name` heading as it walks the document. Six failure modes, each naming the
offending axis, area or ID:

| # | Fails when |
| --- | --- |
| 1 | An axis has ≥1 `Active` requirement and no `#### Coverage` table |
| 2 | A `Status` cell is neither `Covered` nor `Deferred` |
| 3 | A `Covered` area cites no ID, a nonexistent ID, a `Retired` ID, or an ID from another axis |
| 4 | A `Deferred` area has an empty reason |
| 5 | An `Active` requirement is claimed by no area |
| 6 | A `|`-leading line inside a coverage table does not parse as a well-formed row |

Failure mode 6 mirrors the existing malformed-row handling: a bad row is recorded and the scan
continues rather than silently closing the table, which is the defect that shipped as a Critical in
the requirement parser's first version.

### Falsifiability

The check is proven by mutation, not by assertion. Each of these must turn it red naming the
specific axis or ID, and each must be demonstrated:

- deleting an authored axis's coverage table
- deleting one area row while leaving its requirements in place
- changing a `Covered` area's evidence to an ID from another axis

## Scope

This change writes the four ledgers the already-authored axes need: `DECL`, `REL`, `REG`, `LIFE`.
`REG`'s is largely transcription — its three deferred areas already exist as prose with reasons at
`docs/standards/iverson-client-standard.md:187`. The table replaces those three bullets; the
closing paragraph stating that descriptor contents are *not* part of the deferral remains prose,
because it is an anti-claim about another axis's territory rather than an area of `REG`'s own.
`LIFE`'s `#### Known non-conformance` section is unrelated and is not touched.

Deciding `DECL`, `REL` and `LIFE`'s areas is genuine design work, not transcription, and is where
the implementation will spend its time.

## What this check does not do

It cannot detect an area that is **missing** from a ledger. It verifies that every *stated* area is
discharged or deferred, and that every requirement is claimed. An author who omits an area entirely
still passes.

This limit is stated here because the natural misreading — "the gate now proves axis completeness"
— is false. The check converts a silent gap into a written claim; it does not invent the claim.

## Consequences

- Every axis pass in Tasks 8–12 writes two tables instead of one, and must partition its
  requirements across stated areas. This is the cost the check is bought with.
- An axis can no longer grow a requirement that no area claims, nor claim an area nothing
  discharges.
- `REG`'s deferral prose becomes machine-checked rather than advisory.

## Verified assumptions

| # | Assumption | Result |
| --- | --- | --- |
| A1 | `RequirementTableParser` lives in the test project and is the only parser the gate uses | Holds — `Iverson.ClientConformance.Tests/RequirementTableParser.cs:20`, `namespace Iverson.ClientConformance.Tests` |
| A2 | A coverage row would land in `MalformedLines`, breaking check 3 | **FALSE.** The parser opens a table only on the exact header `\| ID \| Status \| Kind \| Statement \|` (`:36`) and closes it at the first non-pipe line (`:50-55`). A `#### Coverage` heading closes the requirement table; the coverage header never opens one; rows are skipped by the `if (!inRequirementTable) continue` guard (`:43-46`). The change is additive |
| A3 | Check 3 fails when `MalformedLines` is non-empty | Holds — `RequirementsCoverageGateTests.cs:145-153` |
| A4 | The parser does not track axis headings | Holds — no heading logic anywhere in `Parse` |
| A5 | IDs encode their axis and a known-axis list exists in the gate | Holds — `IdShapePattern` `^IVC-([A-Z]+)-\d{3}$` and `KnownAxes` at `RequirementsCoverageGateTests.cs:29-31` |
| A6 | Check 1 filters on `Status == "Active"` | Holds — `RequirementsCoverageGateTests.cs:104-107` |
| A7 | All nine axis sections use a uniform `### AXIS — Name` heading | Holds — lines 83, 98, 152, 221, 226, 264, 269, 274, 279 |
| A8 | Exactly four axes have ≥1 `Active` requirement today | Holds — DECL, REL, REG, LIFE. The five empty axes *do* carry requirement-table headers with zero rows, so "authored" must mean ≥1 row, not "has a table" |
| A9 | `REG`'s deferral prose has three areas with reasons that can be transcribed | Holds — reregistration, authorization rules, schema drift, at `iverson-client-standard.md:187-210` |
| A10 | `####` subsections already exist under axes, so `#### Coverage` will not collide | Holds — four exist (lines 127, 187, 212, 251) |
| A11 | Nothing outside the gate tests parses the standard | Holds — only `Requirements.cs` (comment references only) and `RequirementsCoverageGateTests.cs` mention it |
| A12 | Adding a check to `RequirementsCoverageGateTests.cs` is the established pattern | Holds — five checks live there |
| A13 | `Requirements.cs` consts map 1:1 onto `Active` IDs | Holds — enforced by check 1's exact bidirectional match |
| A14 | No existing table in the standard could be misparsed as a coverage table (whole-set check over every pipe table in the document) | Holds — headers are `\| Axis \| Name \| Covers \|` (`:52`), `\| Column \| Meaning \|` (`:68`), and nine `\| ID \| Status \| Kind \| Statement \|`. None resembles `\| Area \| Status \| Evidence \|`; the entry-format table's `\| ID \| ...` row (`:70`) does not match the requirement header either |

## Known issues

- The check cannot detect an omitted area (see *What this check does not do*). Accepted: no
  mechanical check can know an axis's true area set, and inventing one would require a second
  authority the document does not have.
- `DECL` and `LIFE` also lack the backstop assertion the standard's authoring notes make binding
  for every axis (`iverson-client-standard.md:143-150`). That gap predates this design and is not
  addressed here.
