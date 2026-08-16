# The Iverson client standard

**Date:** 2026-08-15
**Status:** Design approved, not yet planned
**Depends on:** `2026-08-09-client-conformance-harness-design.md` — the standard's coverage gate
lives in `Iverson.ClientConformance.Tests`, which exists only on the unmerged
`client-conformance-harness` branch

## Problem

Iverson ships five client libraries — .NET, Java, Python, TypeScript, Go — that must agree on one
wire contract. What a conforming client must do has never been written down. It is inferred, per
initiative, from whatever the last defect exposed.

The cost is a repeating pattern. Relation key typing, foreign-key-only writes, server-generated
ids, mapped-CRUD parity and acting-user identity were each a separate initiative that rediscovered
the same class of problem: one client silently diverging while its own suite stayed green. Each
produced a spec describing the fix. None produced a statement of the rule, so the next divergence
had nothing to be measured against.

The conformance harness closed the *detection* gap. It does not close the *definition* gap: it
encodes requirements as assertions in C#, where they are discoverable only by reading the
orchestrator, and where a requirement nobody thought to assert is indistinguishable from one that
does not exist.

## What this document is

This is the meta-design for the standard, not the standard. It fixes the standard's structure —
where it lives, how requirements are identified, how each is bound to executable evidence — and
drafts one axis in full as a worked exemplar. A subsequent plan authors the remaining axes and the
scenarios they need.

## Decisions taken

Five rulings shape everything below. All were Ben's.

1. **The standard serves as both build spec and audit checklist.** It must be sufficient to write a
   sixth client from, and precise enough to grade the five existing ones against. It therefore
   states the contract independently of any implementation rather than deferring to ".NET's
   behaviour".
2. **It covers every axis a client touches**, untiered. Every requirement is a MUST.
3. **It is made executable by a machine-checked coverage gate** rather than by Gherkin. Gherkin's
   payoff is a shared vocabulary with non-engineering stakeholders, which is not this situation,
   and its linear Given/When/Then fits the harness badly — the harness's central judgement is a
   three-way agreement between the driver's typed object, the orchestrator's gRPC read and the
   Postgres column, across phases that interleave driver and orchestrator steps. What Gherkin
   genuinely buys — a requirement cannot exist without a test — the gate buys directly.
4. **Requirements constrain behaviour and capability, not API shape.** What goes on the wire and
   what must be reachable through the public API are normative; naming and signatures are each
   language's own business. This is the level at which the real divergences have occurred.
5. **The server is the enforcement boundary.** Where a rule can be enforced server-side, the
   requirement is on the server, and client-side checks are recommended diagnostics rather than
   requirements. A rule enforced only by five independent implementations is not enforced.

## Document shape

### Location

`docs/standards/iverson-client-standard.md`. Everything in `docs/specs/` records a decision at a
point in time; the standard is the opposite — continuously true, and amended rather than
superseded. `docs/standards/` does not exist today and is created for it.

### Requirement identity

`IVC-<AXIS>-<NNN>`, three digits, assigned sequentially within an axis. IDs are permanent and never
reused. A withdrawn requirement stays in the document marked `Retired` with its rationale, so a
citation in an old commit or review always resolves. Gaps are expected.

Every declaration carries a status — `Active` or `Retired` — and only `Active` requirements are
subject to the coverage gate.

### Axes

| Axis | Covers |
|---|---|
| `DECL` | entity declaration, key field, tenant field, scalar and array type mapping |
| `REL` | relation kinds, foreign-key inference and naming, navigation properties, hydration |
| `REG` | schema registration, descriptor contents, authorization rules, drift |
| `IDN` | service token, acting-user identity, tenancy enforcement |
| `LIFE` | object lifecycle — mapped create, read, update, delete, server-assigned keys, depth |
| `QRY` | search and aggregate |
| `VEC` | vector search — `SearchSimilar`, `SearchChunks` |
| `SCH` | `GetSchema` |
| `ERR` | error contracts — which failures surface as what, and from which side |

### Requirement kinds

A classification, not a tier. Every requirement is a MUST.

- **Behaviour** — observable on the wire or in stored state. Testable through the driver protocol.
- **Capability** — an operation that must be reachable through the client's public API. Naming and
  signature are the language's own; reachability is not. Testable by asking a driver to perform it
  and having it report whether it could.

### Entry format

ID, status, kind, a one-sentence normative statement, a rationale naming the spec or defect that
produced it, and the conformance evidence — the scenario and assertion that discharge it.

Provenance is a discipline, not decoration: a requirement with no incident or spec behind it is
usually someone's preference wearing normative clothes.

### Recommended diagnostics

One non-normative appendix. Client-side foreign-key naming checks live here: the server is the
enforcement boundary, so a client that also rejects early offers a better-worded error rather than
satisfying a requirement. Keeping it visibly outside the normative body is what stops it drifting
back into a de-facto MUST that three of five clients happen to meet.

## The coverage gate

### Registry

`Iverson.Server/Iverson.ClientConformance/Requirements.cs` holds one `public const string` per
requirement, named for what it asserts and valued as the ID:

```csharp
public const string ExactlyOneKeyProperty = "IVC-REG-004";
```

### Citation

`Assertion` gains an optional trailing field:

```csharp
public sealed record Assertion(string Name, bool Passed, string Detail = "", string? RequirementId = null)
```

Every existing call site compiles unchanged — all construction goes through the `Pass`/`Fail`/`From`
factories, with no positional `new Assertion(`, no deconstruction and no arity-dependent `with`.
Requirement-discharging assertions cite via the const; harness-internal ones pass nothing. There is
no sentinel to abuse, which is why citation is optional rather than compulsory: a required field
would force a `NotApplicable` value onto plumbing assertions, and that value would become the
escape hatch.

### The gate

A unit test in `Iverson.ClientConformance.Tests`, offline and deterministic:

1. **The registry mirrors the document.** A requirement is *declared* only by a row in a requirement
   table; IDs appearing anywhere else in the document are cross-references and are not parsed. The
   set of `Active` declared IDs in `docs/standards/iverson-client-standard.md` must equal the consts
   reflected off `Requirements`. This catches a requirement with no const and a const with no
   requirement. Retired declarations are parsed for well-formedness and ID-uniqueness only, and take
   no const. Reflection selects `IsLiteral && !IsInitOnly`, which yields consts and excludes
   `static readonly`.
2. **Every requirement is cited.** Each const's identifier must appear at least once in
   `Iverson.ClientConformance/` source, excluding `Requirements.cs` itself and excluding the test
   project. Zero occurrences fails, naming the uncited ID. Check 2's set is scoped to `Active`
   requirements by construction, since only those have consts.
3. **Every ID is well-formed** — `IVC-[A-Z]+-\d{3}` with a known axis.

The document is the source of truth for what exists. A requirement added to the standard fails the
build until something tests it. That is the property being bought, and it is deliberate friction.

**Check 2's two exclusions each close a self-match.** The test project is excluded because
assertions are also constructed there (`NavPropertyRejectedScenarioTests`, `InteropScenarioTests`),
and a requirement "cited" solely by a test fixture discharges nothing. `Requirements.cs` is excluded
because it sits inside the scanned directory, so without the exclusion every const would match its
own declaration and the check could never fail — a declaration is not a citation.

**Path resolution.** The test locates the repository root by walking up from
`AppContext.BaseDirectory` to the directory containing `Iverson.slnx`. No existing test reads a
repository file, so this convention is established here.

### Runtime tally

Separate from the gate and complementary to it. Static citation is a build-time property; it proves
a requirement is *mentioned*, not that its assertion ran or could fail. The report therefore records
which requirement IDs each language actually exercised, and which no cell touched.

This requires a change the harness does not currently support. `ReportCell` carries
`(Language, Scenario, Status, Reason, Detail)` and no assertions; scenarios accumulate assertions in
`LanguageState.Assertions` and `Cell()` keeps only the failures, as text. Passing assertions are
discarded. `ReportCell` must carry the full assertion list, which touches all four scenarios'
`Cell()` paths and `RenderJson`. Only `Program.cs:157` consumes the JSON, and no CI or script parses
it, so extending the shape is safe.

### Capability failures are failures

A `skip` is legitimate only for a missing toolchain — `mvn not found`. A driver reporting that its
client cannot perform a required operation is a **FAIL**. Under an untiered standard this is what
turns a documented quirk into a live non-conformance, which is the audit output the standard exists
to produce.

## Worked axis: `REL`

Relations are the exemplar because they carry the most verified ground — the foreign-key-only write
contract, relation key typing and relation foreign-key integrity all landed here — and have produced
more cross-client defects than the rest combined: the `PropertyName`/`ForeignKey` collision, the
`"Ids"` strip gap, Go serializing slices as `null`, one-to-many reverse resolution breaking in three
clients. Machinery that survives `REL` will survive the easier axes.

| ID | Status | Kind | Statement |
|---|---|---|---|
| `IVC-REL-001` | Active | Behaviour | A client synthesizes a foreign-key property for `many_to_one`, `one_to_one` and `many_to_many`, and none for `one_to_many` |
| `IVC-REL-002` | Active | Behaviour | A synthesized foreign-key property is named `{RelatedTypeName}Id` |
| `IVC-REL-003` | Active | Behaviour | A client derives a navigation-property name distinct from the relation's foreign-key name, for every relation kind |
| `IVC-REL-004` | Active | Behaviour | `isArray` is set on the foreign-key property for `many_to_many` and for no other kind |
| `IVC-REL-005` | Active | Behaviour | Write payloads carry foreign-key values only; navigation properties are never sent |
| `IVC-REL-006` | Active | Behaviour | A foreign-key value is readable at every depth, including after hydration |
| `IVC-REL-007` | Active | Behaviour | Multi-valued foreign keys are sent as a list, never a delimited string |
| `IVC-REL-008` | Active | Behaviour | `one_to_many` resolves by reverse foreign-key lookup on the related type |
| `IVC-REL-009` | Active | Capability | A depth-resolved read is reachable through the public API |
| `IVC-REL-010` | Active | Behaviour | Foreign-key values are well-formed UUIDs, and foreign-key columns are typed `UUID` or `UUID[]` |

Every one traces to a defect that shipped.

### Why `IVC-REL-003` covers `many_to_many`

This was the design's one forced decision, and the drafted requirement was initially wrong.

`Verifier.VerifyRegistration` exempts `ManyToMany` from the distinctness check by design, and the
server agrees: where the two names collide, "the payload key IS the foreign key… there is nothing to
reject" (`RelationValidator.cs:18-24`), which notes that Java and .NET can both produce that shape.
Applying the check to `many_to_many` would fail four otherwise-conforming clients.

But `ResolveManyToManyAsync` reads the foreign key from `relation.ForeignKey` and writes the
hydrated list to `entityStruct.Fields[relation.PropertyName]`
(`EntityRelationResolver.cs:106,137`). When those names collide, **hydration overwrites the foreign
key**, and the colliding client cannot satisfy `IVC-REL-006`. The two requirements conflict
directly.

`crud-roundtrip` is green today only because all five drivers acquired distinct `many_to_many` names
via the `"Ids"` strip fix. A legitimately-colliding client would pass registration and fail at
depth, with nothing currently detecting it.

**Ruling (Ben, 2026-08-15): `IVC-REL-003` extends to every relation kind, and is enforced on both
sides.** It makes `IVC-REL-006` unconditional and removes a latent failure.

The requirement had to name *which* side enforces it, because the five clients derive the
navigation-property name by two different mechanisms. Python, TypeScript and Go derive it — they
PascalCase the member name and strip a trailing `Id`/`Ids` with length guards — so the client itself
guarantees distinctness. .NET and Java pass the declared name through: `SchemaRegistrar.cs:85` sets
`PropertyName = relation.Property.Name` verbatim, and `SchemaRegistrar.java:296` PascalCases the
field name with no strip rule at all. For those two, collision is decided by the entity the user
wrote, not by the client, so a requirement phrased as an observation about descriptors would have
graded whichever model the conformance driver happened to declare — while a real .NET or Java user
could register a colliding model and lose the foreign key at depth 1 with the client showing green.

Both sides therefore act:

- **Clients derive.** `IVC-REL-003` obliges the client to produce a distinct navigation-property
  name. .NET and Java gain the strip rule that Python, TypeScript and Go already have.
- **The server rejects.** A descriptor whose `PropertyName` equals its `ForeignKey` is refused at
  registration, closing the hole for any client, including a sixth one that forgets.

This does not contradict ruling 5. The client-side obligation is a derivation rule — behaviour the
client alone can produce — not a duplicate of the server's validation, which remains the enforcement
boundary.

### The `REG` requirements this design creates

Ruling 5 lands as a concrete server change. `SchemaRegistrationOrchestrator.cs:83-109` validates that
a relation's foreign key is a declared property and that its SqlType is `UUID` or `UUID[]`. It never
validates the *name*. Meanwhile `ManyToOneAttribute(Type related, string? foreignKey = null)` lets a
.NET model name its foreign key anything, so a misnamed foreign key registers cleanly from .NET
where Go, Python and TypeScript reject it client-side.

The plan therefore adds a foreign-key naming check to that block, authored as a `REG` requirement.
Client-side checks become recommended diagnostics.

The `IVC-REL-003` ruling adds a second `REG` requirement in the same place: a descriptor whose
relation `PropertyName` equals its `ForeignKey` is rejected at registration, for every relation kind.
`RelationValidator.cs:18-24` currently treats that collision as legitimate; the ruling reverses it.

## Plan scope

The plan authored from this design must deliver:

- The standard document, all nine axes.
- `Requirements.cs`, the `Assertion` citation field, and the three-check gate.
- The `ReportCell` assertion-carrying change and the runtime tally.
- The two `REG` server checks: foreign-key naming, and rejection of a `PropertyName`/`ForeignKey`
  collision.
- The navigation-property derivation rule in the .NET and Java clients, matching the `Id`/`Ids`
  strip that Python, TypeScript and Go already perform.
- **Five new scenarios** — `QRY`, `VEC`, `SCH`, `IDN` and `ERR` — each implemented across five
  drivers. All ~37 existing assertion sites sit in the `DECL`/`REL`/`REG`/`LIFE` cluster; nothing
  today discharges the other five axes, and the gate is strict, so the requirements cannot land
  without them.

The scenario work is the bulk of it and is five-way duplicated by nature.

## Consequences

**Clients will be non-conforming on day one.** That is the audit output working as intended, not a
flaw in the document, and the first full matrix should be read accordingly. Known already:

- Go alone makes `RegisterAll`'s authorization map a required positional parameter
  (`registrar.go:36-39`, no overload) where .NET, Python and TypeScript default it and Java offers a
  no-argument overload.
- .NET accepts an explicit `foreignKey` and registers a misnamed one cleanly, where three clients
  reject it and the server checks nothing.

**The `IVC-REL-003` ruling changes two production clients and tightens the server.** .NET and Java
gain a navigation-property derivation rule, so the descriptors they emit for existing models change
— a wire-visible change, not an internal one. The server begins rejecting `PropertyName`/`ForeignKey`
collisions it has accepted historically, which will refuse descriptors that register cleanly today.

**The standard is deliberately hard to extend.** Adding a requirement means adding a test in the
same change.

**Nothing lands on `main` until the harness does.** The gate lives in
`Iverson.ClientConformance.Tests`, which exists only on `client-conformance-harness` — 42 commits,
unpushed, currently `6c18080`. The standard should be authored on a branch taken from it.

## Verified assumptions

Twenty-three assumptions, enumerated against the design before verification and checked against the
branch at `6c18080`.

| # | Assumption | Result |
|---|---|---|
| A1 | `docs/standards/` is free, with no conflicting home for living documents | ✅ `docs/` holds `specs`, `plans`, `runbooks`, `criticalreviews`; no standards directory |
| A2 | No client-standard document already exists | ✅ grep hits are analysis documents only |
| A3 | `Assertion` tolerates an optional trailing field | ✅ zero `new Assertion(`, no deconstruction, all construction via `Pass`/`Fail`/`From` |
| A4 | A test can resolve a path to `docs/` | ⚠️ **PARTIAL** — no existing test reads a repository file. Design pins the convention: walk up from `AppContext.BaseDirectory` to the directory holding `Iverson.slnx` |
| A5 | Assertions are constructed only in `Verifier.cs` and `Scenarios/` | ⚠️ **PARTIAL** — also in `NavPropertyRejectedScenarioTests` (×6) and `InteropScenarioTests` (×5). Design updated: check 2 scans the orchestrator project only |
| A6 | `Report` can carry requirement IDs into JSON | ❌ **FAILED** — `ReportCell` carries no assertions, and `Cell()` keeps only failures as text (`CrudRoundtripScenario.cs:461-464`); passing assertions are discarded. Design updated: `ReportCell` carries the full assertion list |
| A7 | The four scenarios are `crud-roundtrip`, `naming-rejected`, `nav-property-rejected`, `interop` | ✅ confirmed at each scenario's `Name` const |
| A8 | Reflection over `public const string` yields values at test time | ✅ ran it — `IsLiteral && !IsInitOnly` returned both consts and excluded a `static readonly` |
| A9 | Foreign keys are synthesized for `many_to_one`/`one_to_one`/`many_to_many`, never `one_to_many` | ✅ `one_to_many` exempt on both sides; its foreign key is a column on the related type's row |
| A10 | Foreign keys are named `{RelatedTypeName}Id`; the server does not check the name | ✅ inference confirmed; `SchemaRegistrationOrchestrator.cs:83-109` checks existence and SqlType only |
| A11 | A navigation-property name cannot collide with its foreign key | ❌ **FAILED** — `many_to_many` collision is legitimate (`RelationValidator.cs:18-24`) and `Verifier` exempts it by design. Surfaced the conflict with `IVC-REL-006`; resolved by ruling above |
| A12 | `isArray` is set for `many_to_many` only | ✅ `Verifier` scopes it to the m2m foreign-key set; server requires `UUID[]` for m2m and `UUID` otherwise |
| A13 | Navigation properties in a write payload are rejected with `InvalidArgument` | ✅ `RelationValidator.cs:60,69` |
| A14 | The foreign key is readable at every depth in all five clients | ✅ live `crud-roundtrip` green across five at `6c18080` — but conditional on distinct names; see A11 |
| A15 | Multi-valued foreign keys are sent as a list | ✅ read via `GetFieldStringList`; column typed `UUID[]` |
| A16 | `one_to_many` resolves by reverse foreign-key lookup | ✅ `ResolveOneToManyAsync` → `FetchByColumnAsync` (`EntityRelationResolver.cs:154`) |
| A17 | Only .NET exposes a depth-resolved read | ❌ **FAILED** — all five do: `GetMappedAsync(key, depth)`, `core.py:569`, `core.ts:619`, `EntityCoordinator.java:185`, and Go passes depth through (`coordinator_test.go:766-783`). Mapped-CRUD parity closed it. Removed from the day-one non-conformance list |
| A18 | Go alone requires `RegisterAll`'s authorization map | ✅ `registrar.go:36-39`, no overload; the other four default or overload it |
| A19 | .NET accepts an explicit foreign key and the server never checks naming | ✅ `ManyToOneAttribute(Type related, string? foreignKey = null)`; server validates existence and SqlType only |
| A20 | Search, aggregate, vector search and `GetSchema` exist in all five clients | ✅ verified per client |
| A21 | Existing assertions can discharge each axis | ❌ **FAILED** — all ~37 sites sit in `DECL`/`REL`/`REG`/`LIFE`; nothing discharges `QRY`, `VEC`, `SCH`, `IDN` or `ERR`. Plan grows from three new scenarios to five |
| A22 | `client-conformance-harness@6c18080` is the right base and is clean | ✅ worktree clean |
| A23 | Nothing consumes `Assertion`'s arity or the report's JSON schema externally | ✅ only `Program.cs:157` writes the JSON; no CI or script parses it |
| A24 | Every client derives the navigation-property name, so `IVC-REL-003` is gradable per client | ❌ **FAILED** — only Python, TypeScript and Go derive it. `SchemaRegistrar.cs:85` sets `PropertyName = relation.Property.Name` verbatim and `SchemaRegistrar.java:296` PascalCases the field name with no strip rule, so for .NET and Java the name comes from the user's model. Forced the two-sided ruling above |
| A25 | The path-resolution marker exists at the repository root | ✅ `Iverson.slnx` present at the worktree root, so walking up from `AppContext.BaseDirectory` terminates |
| A26 | The standard document can be committed and read from a fresh clone | ✅ `.gitignore:46-51` ignores `**/docs/specs/`, `**/docs/plans/`, `**/docs/criticalreviews/`, `**/docs/reviews/`, `**/docs/performance/` and `**/docs/superpowers/`, but `git check-ignore` reports `docs/standards/` is **not** ignored |

## Known issues / accepted as out of scope

**The gate proves citation, not falsifiability.** A cited assertion may still be incapable of
failing. That branch's own history is the argument — a committed mutation marker made eight
assertions unfailable while green, and a final review found an extracted helper the production path
never called whose tests looked meaningful. The runtime tally narrows this and mutation testing
remains the real answer; neither is replaced by the gate.

**CI execution is not addressed**, inheriting the harness's position.
