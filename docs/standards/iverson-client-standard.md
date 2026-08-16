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

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

### REL — Relations

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

### REG — Registration

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

### IDN — Identity

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

### LIFE — Lifecycle

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |

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
