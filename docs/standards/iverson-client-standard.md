# The Iverson Client Standard

## What this document is

Iverson ships five client libraries — .NET, Java, Python, TypeScript, and Go — that must all agree
on one wire contract against the same server. This document is the normative statement of that
contract: a numbered list of requirements, each identified by an `IVC-<AXIS>-<NNN>` ID, that every
client MUST satisfy.

Every requirement in this document is a MUST. There is no SHOULD tier and no "recommended"
requirement in the normative body — a requirement is either binding or it does not belong here.
Non-normative guidance (diagnostics a client may choose to run but that the server does not
require) lives in the *Recommended diagnostics* appendix at the end of this document, clearly
marked as non-normative.

Each requirement is bound to executable evidence. `Iverson.Server/Iverson.ClientConformance/Requirements.cs`
declares one `public const string` per `Active` requirement in this document, and the conformance
orchestrator (`Iverson.Server/Iverson.ClientConformance/`) must cite that const from at least one
`Assertion` it constructs. A coverage gate test
(`Iverson.Server/Iverson.ClientConformance.Tests/RequirementsCoverageGateTests.cs`) fails the build
if the set of `Active` requirement IDs in this document and the set of consts in `Requirements.cs`
ever diverge, or if a const exists that no assertion in the orchestrator cites. A requirement with
no citing assertion is not yet enforced, no matter how firmly it reads on the page.

### Scope: behaviour and capability only

Requirements in this document constrain two things only:

- **Behaviour** — what goes on the wire: request/response shapes, field semantics, error
  conditions, ordering guarantees, and other observable effects of an operation.
- **Capability** — what MUST be reachable through a client's public API, so that an application
  built on any of the five clients can rely on the same set of operations being available.

Requirements never mandate a specific method name, type name, parameter order, or other detail of
a client's public signature. Two clients that expose the same capability with different-looking
APIs are both conformant; two clients that disagree on what goes on the wire are not.

### The server is the enforcement boundary

Where a rule can be enforced by the server — validated, rejected, or otherwise made authoritative
server-side — the requirement in this document is written against the server's behaviour, and
conformance is verified by observing what the server does when a client (any client) attempts the
operation. Checks that a client library performs locally, before ever making a call, are useful
defense in depth but are not themselves normative: they belong in the *Recommended diagnostics*
appendix, not in a requirement table.

## The nine axes

Every requirement ID has the form `IVC-<AXIS>-<NNN>`, where `<AXIS>` is one of the following nine
tokens:

| Axis | Name | Covers |
| --- | --- | --- |
| DECL | Declaration | How entity/document types are declared and registered by a client. |
| REL | Relations | Foreign keys, navigation properties, and relation traversal. |
| REG | Registration | Schema registration and reregistration behaviour. |
| IDN | Identity | Acting-user identity resolution and propagation. |
| LIFE | Lifecycle | Server-generated IDs, create/update/delete semantics. |
| QRY | Query | Filtering, pagination, and query construction. |
| VEC | Vector | Vector/embedding-backed search operations. |
| SCH | Schema | Agent-facing schema retrieval. |
| ERR | Errors | Error shapes and propagation across the wire. |

## Requirement entry format

Each requirement is one row in an axis's table:

| Column | Meaning |
| --- | --- |
| ID | `IVC-<AXIS>-<NNN>`, unique across the whole document. |
| Status | `Active` (binding, takes a const in `Requirements.cs`, subject to the coverage gate) or `Retired` (kept for history and ID-uniqueness only; parsed for well-formedness but takes no const and is not subject to coverage). |
| Kind | `Behaviour` or `Capability` (see Scope above). |
| Statement | The requirement itself, stated as a MUST. |

A requirement's rationale and evidence pointer (which orchestrator assertion(s) cite it) are not
columns in the summary table; they are recorded as prose immediately below the table, or deferred
to the `Requirements.cs` const's doc comment once the requirement is implemented. This document
currently declares no requirements — every axis table below is empty. The coverage gate must be
green in this state: an empty set of `Active` IDs compared against an empty set of consts.

## Requirement tables

### DECL — Declaration

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-DECL-001 | Active | Behaviour | A client declares exactly one key property |
| IVC-DECL-002 | Active | Behaviour | A client declares a tenant field that is itself a declared property |
| IVC-DECL-003 | Active | Behaviour | The key property is typed `UUID` |
| IVC-DECL-004 | Active | Behaviour | A key value is a well-formed UUID on every leg — driver, the orchestrator's own gRPC read, and Postgres |
| IVC-DECL-005 | Active | Behaviour | A client's declared tenant field is typed as a scalar string, never `UUID` and never array-typed |
| IVC-DECL-006 | Active | Behaviour | A property declared array-typed never declares its CLR type as a delimited string |

### REL — Relations

Relations are the worked axis: the exemplar for how every other axis in this document is authored.
Each requirement traces to a defect that shipped or a ruling recorded in
`docs/specs/2026-08-15-iverson-client-standard-design.md` ("Worked axis: `REL`"). Full rationale and
the assertion(s) that discharge each requirement are recorded on the corresponding const's doc
comment in `Requirements.cs`, per this document's own convention for an implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-REL-001 | Active | Behaviour | A client synthesizes a foreign-key property for `many_to_one`, `one_to_one` and `many_to_many`, and none for `one_to_many` |
| IVC-REL-002 | Active | Behaviour | A synthesized foreign-key property is named `{RelatedTypeName}Id` |
| IVC-REL-003 | Active | Behaviour | A client derives a navigation-property name distinct from the relation's foreign-key name, for every relation kind |
| IVC-REL-004 | Active | Behaviour | `isArray` is set on the foreign-key property for `many_to_many` and for no other kind |
| IVC-REL-005 | Active | Behaviour | Write payloads carry foreign-key values only; navigation properties are never sent |
| IVC-REL-006 | Active | Behaviour | A foreign-key value is readable at every depth, including after hydration |
| IVC-REL-007 | Active | Behaviour | Multi-valued foreign keys are sent as a list, never a delimited string |
| IVC-REL-008 | Active | Behaviour | `one_to_many` resolves by reverse foreign-key lookup on the related type |
| IVC-REL-009 | Retired | Capability | A depth-resolved read is reachable through the public API |
| IVC-REL-010 | Active | Behaviour | Foreign-key values are well-formed UUIDs, and foreign-key columns are typed `UUID` or `UUID[]` |

`IVC-REL-009` is retired — superseded by the `LIFE` depth capability. What mapped-CRUD parity
actually verified for all five clients was reachability: that a depth-resolved read is reachable
through the client's public API (`IVC-LIFE-006`). It did not verify that the returned entity is
hydrated at that depth — that clause is `IVC-LIFE-007`, and it currently fails for four of the five
clients (see "Known non-conformance" under `LIFE` below). A row's Statement cell is the statement of
record and must stay immutable across retirement; retirement rationale belongs in this prose, never
appended into the statement text.

#### Authoring notes (for future axes)

Because `REL` is the exemplar the remaining eight axes are copied from, three conventions this axis
had to relearn the hard way are recorded here so the next axis does not repeat them:

- **A row's Statement cell is immutable.** It does not change when a requirement retires, gets a
  narrower or wider reading, or acquires a rationale — the Statement is the contract as originally
  stated. Rationale, retirement reasoning, and scoping decisions are prose below the table (or the
  citing const's doc comment once implemented), never text appended into the Statement itself. See
  `IVC-REL-009` above for the corrected form.
- **A requirement ID must be cited as an `Assertion` constructor argument, not merely in a comment
  or doc string.** `RequirementsCoverageGateTests`'s citation check is a substring match over source
  text, so a requirement ID appearing only in a comment would satisfy it without any assertion
  actually failing when the requirement is violated. Every `Requirements.*` reference that is meant
  to discharge coverage must be passed as the `requirementId` argument to `Assertion.From`/`Pass`/
  `Fail`, e.g. `Assertion.From(name, condition, detail, Requirements.RelForeignKeySynthesizedForOwningKinds)`.
- **State which assertion backstops a per-relation loop.** `Verifier.VerifyRegistration`'s
  "declares exactly the expected relation kinds" assertion (fired unconditionally, outside the
  `foreach (var relation in descriptor.Relations)` loop) is what stops a client that silently drops
  a relation from producing a fully green, but empty, result — every assertion inside the loop is
  otherwise vacuously true when the loop runs zero times. This backstop assertion does not itself
  carry a requirement ID (it checks relation *shape*, which no single `IVC-REL-*` statement owns),
  but each axis must have one, and its doc comment must say in plain language what it backstops and
  why, exactly as this paragraph does.

### REG — Registration

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-REG-001 | Retired | Behaviour | The server rejects registration of a relation whose foreign key is not named `{RelatedTypeName}Id` (or `{RelatedTypeName}Ids` for `many_to_many`) |
| IVC-REG-002 | Active | Behaviour | The server rejects registration of a relation whose navigation-property name equals its foreign key, for every relation kind |
| IVC-REG-003 | Active | Behaviour | The server rejects registration of a `many_to_one`, `one_to_one` or `many_to_many` relation whose foreign key is not named `{RelatedTypeName}Id` (or `{RelatedTypeName}Ids` for `many_to_many`) |

Both `IVC-REG-002` and `IVC-REG-003` are the server-side half of a pair whose client-side half is
already normative elsewhere in this document: `IVC-REL-002` obliges a client to derive
`{RelatedTypeName}Id` for a synthesized foreign key, and `IVC-REL-003` obliges a client to derive a
navigation-property name distinct from it. Ruling 5 ("the server is the enforcement boundary") is
why both also get a `REG` requirement rather than resting on the client derivation alone — a
client-side rule with no server-side backstop is enforced only as well as the least careful of five
implementations (or a sixth, not-yet-written one). `IVC-REG-002` and `IVC-REG-003` are what make
that backstop itself part of the standard rather than an unstated assumption behind
`IVC-REL-002`/`IVC-REL-003`.

`IVC-REG-001` is retired — its statement, read literally, is factually wrong. It bound the naming
rule unqualified, over every relation kind, but `SchemaRegistrationOrchestrator.cs`'s naming loop
deliberately excludes `one_to_many`, and correctly so: a `one_to_many` relation's foreign key is
`{ThisTypeName}Id`, not `{RelatedTypeName}Id`, and it lives on the RELATED type's own row (see
`Iverson.Clients/Python/iverson_client/core.py:128-129`), so the server does not and must not
enforce `IVC-REG-001`'s naming rule against it. Taken literally, `IVC-REG-001` would have made a
conforming server non-conformant — it demanded rejection of descriptors the server correctly
accepts. Per the immutable-Statement convention above, the row's Statement cell is left
byte-unchanged and the correction lands as a new requirement, `IVC-REG-003`, scoped the way its
sibling `IVC-REL-001` scopes itself: it names the three relation kinds the naming rule actually
applies to (`many_to_one`, `one_to_one`, `many_to_many`) and excludes `one_to_many` by omission,
matching what the server has always correctly enforced.

#### Deferred coverage (non-normative)

`standard.md`'s own axis table defines REG as "Schema registration and reregistration behaviour."
The rules authored above are the complete REG deliverable the design spec called for, so authoring
only them is not a spec violation — but three things a literal reading of "registration ... and
reregistration behaviour" could include are deliberately NOT authored as requirements here, and are
recorded rather than left as a silent gap:

- **Reregistration.** `Reregistrar.cs` exercises reregistration (registering an already-registered
  type again) on every conformance run, but no assertion cites a requirement ID against that
  behaviour. Reregistration's correctness is exercised as test-harness plumbing, not verified as a
  normative claim.
- **Authorization rules at registration time.** `SchemaRegistrationOrchestrator.cs:54,208`
  accepts and stores `AuthorizationRules` as part of the descriptor, but no requirement in this
  document constrains what the server does with them at registration time.
- **Schema drift.** A `SchemaDriftException` (thrown by `IRecordStoreSchemaManager.ApplySchemaAsync`
  when a re-registration's shape conflicts with the stored schema) surfaces as `FailedPrecondition`,
  but no requirement asserts on that status code or the conditions that produce it.

These three are deferred, not out of scope forever, and a future axis pass may author requirements
for them. **Descriptor contents are explicitly NOT part of this deferral** — what a registered
descriptor contains (relation shape, foreign-key typing, tenant/owner field typing, array typing,
and so on) is already covered by the `DECL` and `REL` axes, whose assertions read the registration
descriptor directly rather than merely exercising the registration call.

#### Backstop assertion (non-normative)

Unlike `REL`'s per-relation loop (see "Authoring notes" above), `REG`'s two assertions
(`IVC-REG-002`, `IVC-REG-003`) are each single-shot: they fire once, against one hand-built
fixture apiece, rather than iterating a `foreach` over a descriptor's relations where a
zero-iteration loop would vacuously pass. There is no loop body whose vacuous-pass case a backstop
assertion would need to catch, so `REG` declares no backstop assertion — the same reasoning applies
to why `DECL` and `LIFE` also currently have none.

### IDN — Identity

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

### LIFE — Lifecycle

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-LIFE-001 | Active | Capability | A mapped create, read, update and delete are each reachable through the client's public API |
| IVC-LIFE-002 | Active | Behaviour | A mapped create returns a key assigned by the server — encoded as a UUIDv7 — never a client-supplied one |
| IVC-LIFE-003 | Active | Behaviour | An update changes the server's stored value, observable in a subsequent read |
| IVC-LIFE-004 | Active | Behaviour | A delete removes the row such that neither the orchestrator's own gRPC read nor the Postgres row finds it afterward |
| IVC-LIFE-005 | Retired | Capability | A depth-resolved read is reachable through the client's public API, and the entity it returns is hydrated at that depth |
| IVC-LIFE-006 | Active | Capability | A depth-resolved read is reachable through the client's public API |
| IVC-LIFE-007 | Active | Behaviour | The entity returned by a depth-resolved read is hydrated at that depth |

`IVC-LIFE-005` is retired — it conflated two separate claims under one requirement. It named
reachability (the call completes and returns an entity) and hydration (the returned entity actually
carries the hydrated relation) as a single statement, so a client that reached the depth-resolved
read but discarded the hydrated data had no way to go green on the half it satisfied. It is split
into `IVC-LIFE-006` (reachability, superseding the retired `IVC-REL-009`) and `IVC-LIFE-007`
(hydration). `IVC-LIFE-006` is what mapped-CRUD parity actually verified across all five clients.
`IVC-LIFE-007` was never verified, and currently fails live for four of the five clients — see
"Known non-conformance" below.

#### Known non-conformance (non-normative)

`IVC-LIFE-007` fails live for the **Python, TypeScript, Go and Java** drivers. Their typed model
classes declare no field to receive a hydrated relation object, so a depth-1 read reaches the server
correctly (satisfying `IVC-LIFE-006`) and the server returns a hydrated nav property, but the
driver's own mapping step (`get_mapped`/`GetMapped`, e.g. Python's `core.py` `_from_struct`, and the
equivalent driver model files in TypeScript, Go and Java) has nowhere to put the hydrated value and
discards it. Only the .NET driver's model has a field for the hydrated nav property and passes.
Fixing this is a separate initiative touching those four SDKs' model shape (adding a typed field per
relation to receive the hydrated object); it is out of scope for this document, which records the
gap as a known non-conformance rather than silently weakening `IVC-LIFE-007`'s statement to match
current behaviour.

### QRY — Query

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

### VEC — Vector

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

### SCH — Schema

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

### ERR — Errors

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

## Recommended diagnostics (non-normative)

The checks in this appendix are not requirements. No client is out of conformance for omitting
them, and none takes a requirement ID or a const in `Requirements.cs`. They are recorded here
because they have proven useful for catching bugs early, client-side, before a call ever reaches
the server — which remains the authoritative enforcement boundary for the corresponding
server-side rule.

- **Client-side foreign-key naming check.** Before sending a create/update call, a client MAY
  verify locally that each foreign-key field on an entity is named `{RelatedTypeName}Id` and warn
  or fail fast if it is not. The server independently enforces the same naming rule on every
  write; a client that skips this local check is still fully conformant, since the server will
  reject a malformed request regardless.
