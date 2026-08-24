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
declares 42 `Active` requirements across nine axes; each takes a const in `Requirements.cs` and is
cited by at least one orchestrator assertion. An axis whose table is still empty remains legal, and
the coverage gate must stay green in that state: an empty set of that axis's `Active` IDs compared
against an empty set of its consts is a match, not a gap. What the gate rejects is a MISMATCH —
an ID with no const, a const with no ID, or an `Active` ID no assertion cites.

## Requirement tables

### DECL — Declaration

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-DECL-001 | Active | Behaviour | A client declares exactly one key property |
| IVC-DECL-002 | Retired | Behaviour | A client declares a tenant field that is itself a declared property |
| IVC-DECL-003 | Active | Behaviour | The key property is typed `UUID` |
| IVC-DECL-004 | Active | Behaviour | A key value is a well-formed UUID on every leg — driver, the orchestrator's own gRPC read, and Postgres |
| IVC-DECL-005 | Retired | Behaviour | A client's declared tenant field is typed as a scalar string, never `UUID` and never array-typed |
| IVC-DECL-006 | Active | Behaviour | A property declared array-typed never declares its CLR type as a delimited string |

`IVC-DECL-002` and `IVC-DECL-005` are retired — the declaration they graded no longer exists. A
row's tenant is server-owned: the server stamps the `__TenantId` column from the acting user's
`tenant_id` claim at the write chokepoint and strips it from every outbound path, so no client
declares a tenant field at all and no tenant field reaches the wire. A requirement obliging a
client to declare one, and a requirement constraining the type of that declaration, are therefore
both unfalsifiable — there is nothing left for either to grade. Their `Statement` cells are
unchanged: a row's Statement is the statement of record and must stay immutable across retirement;
this prose is where the rationale belongs. What replaced them as the live defence of tenancy is
`IVC-IDN-003`, which grades the server's DERIVATION of the tenant from identity rather than any
client's declaration of it.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Key property declaration | Covered | IVC-DECL-001, IVC-DECL-003, IVC-DECL-004 |
| Array-typed property CLR typing | Covered | IVC-DECL-006 |

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
hydrated at that depth — that clause was `IVC-LIFE-007`, retired below and re-authored as
`IVC-LIFE-008`, which passes live for all five clients. A row's Statement cell is the statement of
record and must stay immutable across retirement; retirement rationale belongs in this prose, never
appended into the statement text.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Foreign-key synthesis | Covered | IVC-REL-001, IVC-REL-002, IVC-REL-004, IVC-REL-010 |
| Navigation-property naming | Covered | IVC-REL-003 |
| Write-payload foreign-key-only | Covered | IVC-REL-005 |
| Foreign-key survives hydration | Covered | IVC-REL-006 |
| Multi-valued foreign keys as list | Covered | IVC-REL-007 |
| One-to-many reverse lookup | Covered | IVC-REL-008 |

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
| IVC-REG-004 | Active | Behaviour | The server rejects registration of a descriptor that declares a tenant field |
| IVC-REG-005 | Active | Behaviour | The server rejects registration of a descriptor that names the reserved server-owned tenant column in any name-bearing position |

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

Two limits of that naming rule are recorded here rather than asserted on. First, it is enforced on
every registration but got no load-path treatment, unlike the navigation-property/foreign-key
collision predicate, which `SchemaRegistry.LoadAsync` was deliberately made to log rather than
reject: a deployment that registered, say, `[ManyToOne(typeof(User))] public Guid EditorId` before
this branch rehydrates and keeps working, but its first RE-registration fails with
`must be named 'UserId'` and has no migration path. Second, the rule makes two same-kind relations
to one related type inexpressible — both would have to be named `UserId`, aliasing one column and
silently hydrating `Editor` and `Author` from the same row. No model in this repository trips
either today, and neither is a client-conformance claim, so `IVC-REG-003` states the rule as the
server enforces it and this paragraph is the disclosure.

`IVC-REG-004` and `IVC-REG-005` are the registration-time half of the server owning the tenant
boundary outright (the client-side half, `IVC-DECL-002`/`IVC-DECL-005`, is retired — see the `DECL`
axis). They are TWO requirements rather than one because they are two different server rules with
two different remedies, not one rule seen twice. `IVC-REG-004` is about a field the client MAY NOT
DECLARE: `tenant_field` (proto field 5) is still on the wire for compatibility, and populating it is
now an error, because silently ignoring it would leave the caller believing its declaration enforces
a boundary the server derives for itself; the remedy is to delete the declaration.
`IVC-REG-005` is about a NAME the client may not use: `__TenantId` is the server's own injected
column, and a descriptor addressing it collides with that column — loudly as a duplicate-column DDL
failure on a new table, but SILENTLY on an already-created one, where the ADD is skipped and the
client's own member never round-trips and is invisible in `GetSchema`; the remedy is to rename the
member. A client can trip either without the other.

`IVC-REG-005` is ONE requirement over SIX addressing sites, not six requirements. The sites —
a scalar property, the key property, a relation's foreign key, a relation's navigation property,
`authorization.owner_field`, and an `authorization.field_permissions[].field_name` — are the closed
enumeration of every string on `TypeDescriptor` (and everything it transitively contains) that can
name a column or become a payload key; the enumeration itself is recorded in
`SchemaRegistrationOrchestrator.RejectReservedTenantName`'s doc comment, which also records, for
every remaining name-bearing field, the construction that makes it unable to reach the reserved
name. One rule ("this name is reserved"), one message shape, one remedy shape, six places it is
applied — so one requirement, with one assertion per site so that a site losing its guard reddens on
its own rather than hiding behind the other five.

**Both are graded orchestrator-side, and deliberately not through the driver channel.** The
`tenant-rejected` scenario hand-builds raw `TypeDescriptor`s in orchestrator-side C# and posts them
to `RegisterSchema` directly — the mechanism `NamingRejectedScenario` and
`NavPropertyRejectedScenario` already use — which grades the SERVER's rule without requiring any
client to express the violation. The two rules reach that channel for different reasons, and the
difference is worth stating rather than blurring.

`IVC-REG-004` **cannot** be graded through the driver channel. No conformance driver can produce a
request that trips it: four clients omit `tenant_field` entirely and TypeScript sends the proto
default `''` (ts-proto types the field as required, so it cannot be omitted the way the other four
omit it), which the guard treats as absent. A requirement graded through that channel would be
unfalsifiable by construction — the same defect the `IDN` axis's own notes describe. The rule's live
value is against STALE CLIENT BUILDS and hand-rolled callers, and a raw orchestrator-side descriptor
is exactly one of those.

`IVC-REG-005` **could** be tripped by a client and is graded orchestrator-side by CHOICE. `__TenantId`
is a legal member name in more than one of the five host languages, which is precisely why the server
guard has to exist at all — this is not a violation nobody can express. What makes the driver channel
the wrong place for it is that the harness's five driver model sets are SHARED across every scenario:
poisoning one to trip a registration guard would break every other scenario that registers the same
type, so grading it there would mean six new fixture types in five languages — thirty models — to
observe one server rule that is identical for every caller. One orchestrator-side descriptor per site
grades exactly the same rule, and the six sites stay in one place where the enumeration behind them
is legible. If a future client is found to DERIVE the reserved name (rather than have a user type it
literally), that is a client-side claim and belongs on `DECL`, not here.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Navigation-property/foreign-key collision rejected at registration | Covered | IVC-REG-002 |
| Foreign-key naming enforced at registration | Covered | IVC-REG-003 |
| Client-declared tenant field rejected at registration | Covered | IVC-REG-004 |
| Reserved server-owned tenant column name rejected at registration | Covered | IVC-REG-005 |
| `one_to_many` foreign keys validated at registration | Deferred | `SchemaRegistrationOrchestrator.cs:103` filters the relation loop with `.Where(r => r.Kind != RelationKind.OneToMany)`, which excludes `one_to_many` from ALL THREE checks in that loop — not only the naming rule `IVC-REG-003` correctly exempts it from, but also foreign-key-is-a-declared-property and foreign-key SQL typing. So a `one_to_many`'s declared foreign key is validated by nothing: `Author` may declare `[OneToMany(typeof(Article), ForeignKey = "WriterId")]` while `Article` declares `AuthorId`, registration succeeds, and every depth-resolved read then queries a column that does not exist. This carve-out is one this axis itself created when it scoped `IVC-REG-003` to the three kinds the server enforces, so it is disclosed here rather than left silent. Closing it is a SERVER change, not a client one, and not a trivial one: the property and typing checks would have to run against the RELATED type's descriptor, which is not in hand when the declaring type registers — it needs either a deferred cross-type validation pass or a registration-order constraint. Its own initiative; no requirement here asserts against it. |
| Reregistration | Deferred | `Reregistrar.cs` exercises reregistration (registering an already-registered type again) on every conformance run, but no assertion cites a requirement ID against that behaviour. Reregistration's correctness is exercised as test-harness plumbing, not verified as a normative claim. |
| Authorization rules at registration time | Deferred | `SchemaRegistrationOrchestrator.cs:54,208` accepts and stores `AuthorizationRules` as part of the descriptor, but no requirement in this document constrains what the server does with them at registration time. |
| Schema drift | Deferred | A `SchemaDriftException` (thrown by `IRecordStoreSchemaManager.ApplySchemaAsync` when a re-registration's shape conflicts with the stored schema) surfaces as `FailedPrecondition`, but no requirement asserts on that status code or the conditions that produce it. |

#### Deferred coverage (non-normative)

`standard.md`'s own axis table defines REG as "Schema registration and reregistration behaviour."
The rules authored above are the complete REG deliverable the design spec called for, so authoring
only them is not a spec violation — but three things a literal reading of "registration ... and
reregistration behaviour" could include are deliberately NOT authored as requirements here, and are
recorded rather than left as a silent gap — reregistration, authorization rules at registration
time, and schema drift; see the Coverage table above for each's reason.

These three are deferred, not out of scope forever, and a future axis pass may author requirements
for them. **Descriptor contents are explicitly NOT part of this deferral** — what a registered
descriptor contains (relation shape, foreign-key typing, owner field typing, array typing, and so
on) is already covered by the `DECL` and `REL` axes, whose assertions read the registration
descriptor directly rather than merely exercising the registration call. Tenant field typing was
in that list until `IVC-DECL-002`/`IVC-DECL-005` were retired; there is no client-declared tenant
field left to type, and what replaced it is `IVC-REG-004`'s outright rejection above.

#### Backstop assertion (non-normative)

Unlike `REL`'s per-relation loop (see "Authoring notes" above), none of `REG`'s assertions can go
vacuous, and that — and only that — is why `REG` declares no backstop assertion. The reason is not
that they are outside a `foreach`: `IVC-REG-002`'s citations iterate
`NavPropertyRejectedScenario.CollisionFixtures` and `IVC-REG-005`'s iterate
`TenantRejectedScenario.ReservedNameFixtures`, so two of the four DO sit in a loop. (An earlier
version of this paragraph asserted otherwise; it was already wrong about `IVC-REG-002` when it was
written.) The reason is what those loops iterate. `REL`'s loop walks a DESCRIPTOR's relations — a
runtime-supplied collection that a fixture change can empty without touching the assertion, which is
exactly the vacuity `REL`'s backstop exists to catch. `REG`'s loops walk `static readonly` fixture
lists authored in the assertion's own file, so emptying one is a source edit to the list three lines
above the `foreach`, not a data condition arising elsewhere. `IVC-REG-003`'s and `IVC-REG-004`'s
assertions are single-shot against one hand-built fixture apiece and are not in a loop at all.

That reasoning is specific to `REG` and must not be generalised to the other backstop-less axes.
`DECL` in particular does have a loop-bodied citation: `IVC-DECL-006`'s only citation
(`Verifier.cs:300-304`) sits inside `foreach (var property in descriptor.Properties.Where(p => p.IsArray))`
(`Verifier.cs:288`), which runs zero times for a descriptor declaring no array property. What keeps
that from being a live hole is not the absence of a loop but a neighbouring assertion:
`IVC-REL-004`'s `isArray` check (`Verifier.cs:279-283`) fires unconditionally per many-to-many
relation and goes red on the only way that loop can empty out for the current fixtures — a
many-to-many foreign key that lost its `isArray` flag. `DECL-006` is therefore protected by
`REL-004`, not by being structurally unfalsifiable, and a fixture set without a many-to-many
relation would remove that protection.

The coverage gate's Check4 does not verify any of this: it checks the Coverage ledgers, not
backstop declarations, so a future axis can restate the sentence above without anything catching it
if it is false there. Extending Check4 to bind backstop claims is a follow-up, not a claim this
axis makes.

### IDN — Identity

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-IDN-001 | Active | Behaviour | A client carries the service identity and the acting-user identity as two distinct credentials on one call, and a mapped write carrying both is accepted |
| IVC-IDN-002 | Active | Behaviour | A row written under an acting user is readable back by that same acting user through the mapped read path, carrying the owner identity that acting user propagated |
| IVC-IDN-003 | Active | Behaviour | The server derives a row's tenant from the acting-user identity rather than from the write payload, and denies an acting user of another tenant who attempts to write that row |
| IVC-IDN-004 | Retired | Behaviour | A row read back through any mapped path carries no server-owned tenant column |
| IVC-IDN-005 | Active | Behaviour | A mapped point read of a row returns no field whose name matches the server-owned tenant column, compared case-insensitively |

`IVC-IDN-001` is the two-credential claim every other axis silently rests on. The service identity
rides in `authorization` and carries the scopes (`admin`, `schema_admin`) the server evaluates
service-side; the acting-user identity rides in `x-acting-user-authorization` and is the end-user
principal row and field authorization is evaluated against (`ActingUserInterceptor.cs`). They are
two tokens for two different subjects on the same call, and a client that collapses them into one
header — or sends only the service half — registers schemas successfully and is then denied every
write with `actor=unknown`, a failure that surfaces phases away from its cause. The requirement is
graded on the write actually being accepted, which is the only observation that requires both
halves to have arrived and been read as different identities.

`IVC-IDN-002` and `IVC-IDN-003` are the propagation half and the enforcement half of what the
acting-user identity is FOR, split the way `QRY` splits `IVC-QRY-001` from `IVC-QRY-002`: a client
that propagates an acting user the server can read, but under which the server's tenancy scoping
does not actually hold, is non-conformant in a way a single conflated requirement would report only
as one undifferentiated red cell.

**`IVC-IDN-002`'s owner clause is a round-trip claim, not a server-derivation claim**, and its
Statement is worded that way — "the owner identity that acting user PROPAGATED". The distinction
matters because the tenant clause of `IVC-IDN-003` beside it IS a derivation claim, and the two are
observed very differently. The harness's acting user holds `iverson-loadtest-bypass`, which the
orchestrator's re-registration grants `CanWriteAll`; `RowFieldAuthorizationEvaluator` therefore
reports `ownershipRequired: false` and `AuthorizationFieldMasking.EnforceWriteAuthorization` never
force-sets the owner column, so the value read back is the one the driver sent, compared against the
same `--owner-id` the driver was given. Verified live: a driver stamping a made-up owner reads that
made-up owner back, while the tenant column in the same run is force-set correctly. The clause is
still worth asserting — a client that mangles, drops or re-cases the owner on its own write or read
path fails it — but it is not evidence that the server derived the owner from the token, and the
requirement must not be read as though it were. Observing owner derivation needs an acting user
without a bypass role; see the Coverage table below. The row's Statement is left byte-unchanged, per
this document's immutable-Statement convention (see `REL`'s authoring notes): the statement as
written is true of what is observed, and the correction belongs in this prose rather than in the
Statement cell.

**`IVC-IDN-003`'s enforcement clause cannot tell "denied because of WHO is calling" from "denied
because NOBODY is calling".** `PermissionDenied` (7) is the server's answer to several distinct
refusals on this path, and it carries the same message for all of them — `"Not authorized to update
this entity."`, the single `deniedMessage` `ObjectMappingGrpcService.Update` passes into every branch
of `AuthorizationFieldMasking.EnforceWriteAuthorization` — and no trailers. A driver that attaches no
acting-user header at all is therefore graded green by this assertion: `ActingUserInterceptor`
returns early on an empty header, the acting-user principal is null, and the evaluator denies. The
difference exists only in the server's own audit log (`reason=TenantMismatch` versus
`reason=AccessDenied`, with `actor=unknown tenant=unknown`), which no client can read. Both halves of
that were verified live, byte for byte, from what the driver itself received. See the Coverage table
below; the gap is not closed by having drivers self-report that they attached the header, since a
library that silently DROPPED the header would still have its driver report success — the assertion
would be worthless in exactly the case it exists for.

`IVC-IDN-003` is verified in both directions, and neither direction is gradeable from a payload the
client controls:

- **Derivation, observed where no client can see it.** The row's real tenant lives in the
  server-owned `__TenantId` column, which the server injects into every schema and strips from every
  outbound path — so it is not on the wire, not in `GetSchema`, and unreachable from any client
  library by design. The derivation is therefore graded by the ORCHESTRATOR, not by a driver's
  read-back: `IdentityScenario` probes Postgres directly (`PostgresProbe.FetchRowAsync`) for the row
  each driver seeded and asserts `__TenantId` carries the acting user's own tenant, force-set from
  the token's `tenant_id` claim on a create (`AuthorizationFieldMasking.EnforceWriteAuthorization`'s
  no-existing-row branch). Two things make that more than a bare presence check. Every driver stamps
  a deliberately wrong tenant value — shared verbatim across all five — into an ORDINARY user column
  it declares called `TenantId`, and the probe asserts that value is still sitting in that user
  column and did NOT become the row's tenant: a NEGATIVE CONTROL against the server taking the
  client's word for it from a column that merely looks like a tenant field. And the probe is
  conjoined with the orchestrator's own gRPC read of the same row, which must show `__TenantId`
  ABSENT — proof that the probe is reading something gRPC genuinely cannot see, without which a
  Postgres-only assertion could not distinguish "the server derived it" from "the client sent it".
  That gRPC-absent half does NOT grade this Statement — it grades the outbound strip, a separate
  claim — so it cites `IVC-IDN-005` and not `IVC-IDN-003`. Letting `IVC-IDN-003` cite it would
  quietly widen this requirement to cover a rule it does not state. A server that took the client's
  word for it, a server that stopped injecting the column, or a client that propagated no acting
  user at all each fail here rather than agreeing by construction with what the driver sent.
- **Enforcement.** The orchestrator mints a SECOND acting-user token, for a different, active
  tenant (`TokenBroker.GetOtherTenantActingTokenAsync`), and passes it to every driver as
  `--wrong-acting-token`. Each driver attempts a mapped update of the row it just created while
  carrying that token in place of its own, and reports the gRPC status code it received as data —
  it judges nothing. The orchestrator asserts the code is `PermissionDenied` (7). All five drivers
  attempt the same operation against the same server and are graded against the same numeric
  constant, so the requirement is simultaneously a per-client correctness claim and a cross-client
  agreement claim: a language that propagates the wrong-user token incorrectly (or not at all)
  disagrees with the other four and its cell alone goes red.

The update the negative leg sends still carries the ACTING user's own tenant in its ordinary
`TenantId` user column, even though the create carries a deliberately wrong one. That WAS
load-bearing and is now merely harmless, and the change is worth recording because the old reason is
still quoted in the drivers' own comments. It used to be that on an EXISTING row the server rejected
a payload tenant differing from the caller's claim as "Tenant field is immutable" — also
`PermissionDenied` (7), fired for ANY caller including the right one — so a negative leg sending the
wrong tenant would have gone green for a client that propagated its own token instead of the wrong
one, proving nothing about identity. That check compares
`AuthorizationDecision.TenantColumn` against the value the payload carries under that name. For any
type registered by a current server build that column is `__TenantId`, and a payload carrying
`__TenantId` is rejected outright with `InvalidArgument` at the top of the same method, several
branches earlier — so against a freshly registered type the immutability branch cannot fire.

**The branch is NOT dead code.** `SchemaRegistry.LoadAsync` rehydrates pre-cutover `_iverson_schema`
rows verbatim, with no normalisation, and those rows persisted `TenantColumn` as the CLIENT-DECLARED
`tenant_field` name — typically `TenantId`. A legacy row registers with a client-declared NAME, not
with a null: `SchemaDescriptor.TenantColumn` is `public required string` and `LoadAsync` refuses to
admit any row whose `tenantColumn` is null or empty, so what reaches the evaluator from a legacy row
is the string `TenantId`. `RowFieldAuthorizationEvaluator`'s `string.IsNullOrEmpty(schema.TenantColumn)`
short-circuit therefore does NOT fire for it, and the `InvalidArgument` guard does not match it
either, because that guard is keyed on the reserved `__TenantId` spelling. On a deployment upgraded
with such rows still present and a type not yet re-registered, a payload carrying `TenantId` passes
the `InvalidArgument` guard, reaches the immutability check with a non-null attempted tenant, and is
denied with `TenantImmutable` — today. Deleting the branch would silently convert that denial and
its audit record into a successful update.

(`AuthorizationDecision.TenantColumn` — a different property — *is* nullable, and for an unrelated
reason: null there means "this decision established no tenant boundary", which every denied path
produces against a perfectly current schema.)

What is true HERE is narrower: the conformance harness registers its types fresh against the build
under test, so its `TenantColumn` is always `__TenantId` and this leg is insensitive to what the
payload's `TenantId` user column says. The only refusal left on this leg is therefore the tenant
MISMATCH between the existing row's `__TenantId` and the wrong acting user's own claim — which is
the refusal this requirement wants.

The status code is reported and compared as the numeric gRPC code, never as a name: the five
languages spell the same code five ways (`PermissionDenied`, `PERMISSION_DENIED`, `7`), so a
name-based comparison would report a cross-language spelling difference as a conformance failure.

`IVC-IDN-004` is retired and re-authored as `IVC-IDN-005`. Its Statement said "any mapped path",
which is wider than the single observation that grades it. One assertion is involved — the third in
`IdentityScenario.JudgeTenantDerivation` — and it makes exactly one `ObjectMapping.Get` at
`Depth = 0`, for one row of one type. A regression that stripped the column from that point read but
emitted it on a depth-1 hydrated child, on a search projection, or on the mapped list would leave
`IVC-IDN-004` green while a row read back through a mapped path carried the column. The retirement
is for that gap alone; the earlier scoping note in this section was already careful to say the claim
is "a mapped read-back, not `GetSchema`, not a search projection", but the Statement cell itself did
not say it, and the Statement cell is what binds.

The correction is deliberately narrower rather than the check being widened. Widening would mean
authoring a claim no assertion in this harness discharges — the precise defect this document's
coverage ledger exists to surface — and grading the wider claim needs new observations (a depth-1
read, a search projection) that belong to whoever authors them, not to a wording fix. If those
observations are added later, `IVC-IDN-005` is the row to extend by authoring siblings beside it.

`IVC-IDN-005` is a WIRE claim, and it is the only requirement in this document that constrains what
the server may EMIT rather than what a client must do. It is at home on `IDN` by the axis's own
precedent — `IVC-IDN-003` already grades a SERVER derivation, not a client capability — and it needs
no tenth axis: the column it is about exists only because of the identity model this axis owns.

It is graded by the third assertion in `IdentityScenario.JudgeTenantDerivation`: the orchestrator's
own gRPC point read of the same row the Postgres probe found `__TenantId` in must come back with no
field matching that name, compared case-INSENSITIVELY so a re-cased `__tenantid` cannot satisfy it —
which is why the Statement names the comparison rather than leaving it to the assertion. That
assertion is the same one that conjoins `IVC-IDN-003`'s Postgres probe — one observation, two claims
graded from it, which is why the two requirements live in one cell — but it cites `IVC-IDN-005`
ALONE.

A client cannot make this requirement fail or pass — it has no lever on it. It is authored anyway
because the strip is the guarantee every client-facing tenancy claim rests on: were it to regress,
every driver in every language would start receiving a column it must never see, and without a
requirement owning it the harness would observe the regression while the standard reported full
coverage.

Token acquisition, suspended and deleted tenants, and field-permission narrowing by acting-user
role are deliberately not authored here; see the Coverage table below.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Service and acting-user identities carried as two credentials on one call | Covered | IVC-IDN-001 |
| Acting-user propagation observable in the stored row | Covered | IVC-IDN-002 |
| Tenancy derived from the acting user and enforced against another tenant's acting user | Covered | IVC-IDN-003 |
| A mapped point read never carrying the server-owned tenant column | Covered | IVC-IDN-005 |
| Token acquisition | Deferred | Every client can mint a service token from a client-credentials trio, but the harness passes a pre-minted `--service-token` to all five drivers on purpose (Authentik stamps the JWT's `iss` from the request's Host header and grants scopes only when asked, neither of which a driver's own minting expresses), so no assertion observes a client's token acquisition and no requirement constrains it. |
| Suspended and deleted tenants | Deferred | `ActingUserInterceptor` rejects an acting user whose tenant is absent, `suspended` or `deleted` with `PermissionDenied`, but the harness runs entirely inside two active tenants and provisions none, so no assertion observes a suspended or deleted tenant and no requirement constrains that path. |
| Field-permission narrowing by acting-user role | Deferred | `RowFieldAuthorizationEvaluator` narrows writable and readable fields by the acting user's `groups` claim, but the harness registers no `FieldPermission` (the `Reregistrar` sets row permissions only), so no assertion observes field narrowing and no requirement constrains it. |
| Owner column derived from the acting user | Deferred | `IVC-IDN-002`'s owner clause observes a round trip, not a derivation: the harness's acting user holds `iverson-loadtest-bypass` (granted `CanWriteAll` by the orchestrator's re-registration), so `RowFieldAuthorizationEvaluator` reports `ownershipRequired: false` and the server never force-sets the owner column — a driver stamping a made-up owner reads it straight back. No assertion observes owner derivation and no requirement constrains it. Closing it needs an acting user without a bypass role on this type, which is a stack-provisioning change, not a wording change. |
| Distinguishing "denied for WHO is calling" from "denied because NO acting user was attached" | Deferred | `IVC-IDN-003`'s enforcement clause grades the numeric status code, and the server answers both refusals with `PermissionDenied` (7), the identical message (`"Not authorized to update this entity."`) and no trailers — so a driver that attaches no acting-user header is graded green by it. Verified live from what the driver received. The distinction exists only in the server's audit log (`reason=TenantMismatch` versus `reason=AccessDenied`), which no client can read, so no assertion can observe it. Closing it needs the server to distinguish the two refusals on the wire; a driver self-report would be worthless, since a library that silently dropped the header would still report success. |
| Ownership enforcement between two acting users of the SAME tenant | Deferred | `AuthorizationFieldMasking` denies an owner mismatch on an existing row exactly as it denies a tenant mismatch, but the only second acting-user identity the dev stack provisions belongs to a different tenant, so the tenant check fires first and no assertion can observe the owner check in isolation. Authoring it needs a second identity inside the acting user's own tenant, which is a stack-provisioning change, not a wording change. |

#### Backstop assertion (non-normative)

`IDN`'s negative leg is only a negative leg while the row it targets exists. The wrong-tenant
acting user's update is denied because `ObjectMappingGrpcService.Update` finds an existing row
whose tenant is not that user's; if the write phase had produced no row, the very same call would
take `EnforceWriteAuthorization`'s no-existing-row branch, be treated as a create, and **succeed** —
turning a denial assertion into a green cell that proves nothing about tenancy.
`IdentityScenario.Judge`'s "the write phase reported a row key for this language" assertion is
therefore `IDN`'s backstop. It fires unconditionally, on every language, before and outside both
the read-back and the denial assertions. Like `REL`'s, `QRY`'s, `SCH`'s and `VEC`'s it carries no
requirement ID: no `IVC-IDN-*` statement owns "this language seeded a row" as such — it is a
property of the harness's own fixture, not of a client — and it is strictly weaker than
`IVC-IDN-002` and `IVC-IDN-003` wherever either can fail.


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
| IVC-LIFE-007 | Retired | Behaviour | The entity returned by a depth-resolved read is hydrated at that depth |
| IVC-LIFE-008 | Active | Behaviour | The entity returned by a depth-resolved read carries the related object's data, including that object's own key and not only the foreign key |

`IVC-LIFE-005` is retired — it conflated two separate claims under one requirement. It named
reachability (the call completes and returns an entity) and hydration (the returned entity actually
carries the hydrated relation) as a single statement, so a client that reached the depth-resolved
read but discarded the hydrated data had no way to go green on the half it satisfied. It is split
into `IVC-LIFE-006` (reachability, superseding the retired `IVC-REL-009`) and `IVC-LIFE-007`
(hydration). `IVC-LIFE-006` is what mapped-CRUD parity actually verified across all five clients.
`IVC-LIFE-007` was never verified live before its own retirement, below.

`IVC-LIFE-007` is itself retired and re-authored as `IVC-LIFE-008`. Its statement — that the
returned entity "is hydrated at that depth", graded by finding a navigation property carrying an
object with its own key — encodes .NET's object shape: a navigation member the client materializes
under a particular name. That is what made four clients that reach the depth-resolved read and
actually materialize the hydrated data fail it, because their model shape differs from .NET's.
`IVC-LIFE-008` is framed as an observable property of what the operation returns — that the entity
carries the related object's data, including that object's own key and not only the foreign key —
rather than as a claim about a member's reachability or name. `Behaviour` is the correct Kind for
this framing: `Behaviour` covers other observable effects of an operation, whereas `Capability` is
reachability, and `IVC-LIFE-006` already holds `Capability` for the depth-resolved read itself.
Framing the successor as reachability would have made the two rows share a Kind and carry their
whole distinction in prose, which is what `IVC-LIFE-005` was retired for. The statement names no
member, type or signature detail, so each client satisfies it in whatever shape its language
allows.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Mapped CRUD reachability | Covered | IVC-LIFE-001 |
| Server-assigned key on create | Covered | IVC-LIFE-002 |
| Update observable in a subsequent read | Covered | IVC-LIFE-003 |
| Delete removes the row on every leg | Covered | IVC-LIFE-004 |
| Depth-resolved read reachability | Covered | IVC-LIFE-006 |
| Depth-resolved read hydration | Covered | IVC-LIFE-008 |

### QRY — Query

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-QRY-001 | Active | Capability | A filtered search is reachable through the client's public API |
| IVC-QRY-002 | Active | Behaviour | A filtered search returns exactly the rows whose stored values match the filter |
| IVC-QRY-003 | Active | Capability | An aggregation over a filtered set is reachable through the client's public API |
| IVC-QRY-004 | Active | Behaviour | An aggregation over a filtered set reports a value computed from exactly the rows that filter matches |

`IVC-QRY-001`/`IVC-QRY-003` are the reachability half and `IVC-QRY-002`/`IVC-QRY-004` the content
half, split the way `SCH` splits `IVC-SCH-001` from `IVC-SCH-002` and for the same reason: search
and aggregation are two distinct RPCs (`Search` streams rows, `Aggregate` returns one unary
response), and a client that can reach one but returns the wrong content from it is non-conformant
in a way a single conflated requirement would report only as one undifferentiated red cell.

`IVC-QRY-002` says *exactly* the matching rows, not *at least* them. Both directions are verified —
a seeded row the search failed to return, and a returned row the run never seeded, are each a
failure. The exact form is checkable because the `query` scenario seeds its rows under a
run-unique marker value and filters on that marker: no row from any earlier run, and no row
written by any other scenario, can match it. All five clients issue the same filter over the same
seeded rows, so the requirement is simultaneously a per-client correctness claim and a
cross-client agreement claim — a language whose filter is built wrongly disagrees with the other
four and its cell alone goes red.

`IVC-QRY-004` grades the aggregate against the harness's own count of the rows it seeded and
observed keys for, never against what the search step of the same run reported. Grading the
aggregate against the search would make the two requirements agree by construction whenever a
client got both wrong in the same direction.

`Search` and `Aggregate` are served from the StarRocks projection, which a mapped write reaches
asynchronously through the outbox. The scenario therefore waits for the projection before its read
phase — a bounded poll with an explicit timeout, reported as a failed step when it expires. It is
never an indefinite wait and never a fixed sleep presented as determinism; see
`ProjectionWaiter.cs`.

Pagination, sort order, joins, bucketing aggregations and `HAVING` are deliberately not authored
here; see the Coverage table below.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Filtered-search reachability | Covered | IVC-QRY-001 |
| Filtered-search result set | Covered | IVC-QRY-002 |
| Aggregation reachability | Covered | IVC-QRY-003 |
| Aggregation value | Covered | IVC-QRY-004 |
| Pagination and sort order | Deferred | Every client's query builder can express paging and sort (`page`/`limit`/`offset`, `orderBy`), but the `query` scenario seeds one row per language — far below any page boundary — so no assertion observes a page boundary or an ordering, and no requirement constrains them. Authoring them needs a seed set large enough that a wrong page size or a dropped sort is observable, which is a scenario change, not a wording change. |
| Joins and multi-type queries | Deferred | `JoinSpec` is expressible from all five builders, but the scenario's subject type is relation-free on purpose — which is what keeps `IVC-QRY-002`'s exact result-set comparison free of hydration effects — so no assertion observes a join and no requirement constrains one. |
| Bucketing aggregations and HAVING | Deferred | Only the scalar count metric is exercised. `TERMS`/`DATE_HISTOGRAM`/`RANGE` bucket output and `HAVING` clauses reference server-fixed output aliases (`bucket_key`, `doc_count`, `metric_val`) that no assertion currently observes, so no requirement constrains them. |
| Vector-backed query paths | Deferred | `SearchSimilar` and `SearchChunks` are served from Qdrant rather than StarRocks and belong to the `VEC` axis, not this one. |

#### Backstop assertion (non-normative)

`QRY`'s content assertions (`IVC-QRY-002`, `IVC-QRY-004`) compare what a client reported against
the set of row keys the harness itself observed the write phase produce. If that expected set were
empty — every write denied, or every driver silently reporting no key — the set comparison would
succeed against an empty result and the aggregate would match a count of zero: five clients
agreeing on nothing, rendered green. `QueryScenario.Judge`'s "the run seeded at least one row for
this query to match" assertion is therefore `QRY`'s backstop. It fires unconditionally, on every
language, before and outside the comparisons, and is exactly the positive expected row count this
axis's scenario requires. Like `REL`'s and `SCH`'s it carries no requirement ID: no `IVC-QRY-*`
statement owns "the run seeded something" as such — it is a property of the harness's own fixture,
not of a client — and it is strictly weaker than `IVC-QRY-002` wherever `IVC-QRY-002` can fail.

### VEC — Vector

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-VEC-001 | Active | Capability | A vector similarity search is reachable through the client's public API |
| IVC-VEC-002 | Active | Behaviour | A vector similarity search returns exactly the rows its accompanying scalar filter matches |
| IVC-VEC-003 | Active | Capability | A chunk search is reachable through the client's public API |
| IVC-VEC-004 | Active | Behaviour | A chunk search returns chunks belonging to exactly the parent rows its accompanying filter matches |

`IVC-VEC-001`/`IVC-VEC-003` are the reachability half and `IVC-VEC-002`/`IVC-VEC-004` the content
half, split the way `QRY` splits `IVC-QRY-001` from `IVC-QRY-002` and for the same reason:
`SearchSimilar` and `SearchChunks` are two distinct RPCs against two distinct Qdrant collections
(the object collection's named vectors, and the `_chunks` collection's passage vectors), returning
two different response shapes — a client that can reach one but gets the wrong content back from it
is non-conformant in a way a single conflated requirement would report only as one undifferentiated
red cell.

Both content requirements say *exactly*, not *at least*. Both directions are verified — a seeded row
the search failed to return, and a returned row the run never seeded, are each a failure. The exact
form is checkable because the `vector-search` scenario stamps a run-unique marker on every row it
seeds and every driver sends that marker as the filter accompanying its query: `SearchSimilar`
filters on it as a scalar payload clause, and `SearchChunks` filters on it as a metadata column
denormalized onto every chunk point (`IntelligenceStoreConsumer`). No row from any earlier run, and
no row written by any other scenario, can match. All five clients issue the same query text with the
same filter over the same seeded rows, so each requirement is simultaneously a per-client
correctness claim and a cross-client agreement claim — a language whose request is built wrongly
disagrees with the other four and its cell alone goes red.

Neither content requirement grades a client against its own other report. `IVC-VEC-002` compares the
row labels a driver's similarity search returned against the labels the harness itself expects for
the languages whose write phase reported a key, and `IVC-VEC-004` compares the parent keys a
driver's chunk search returned against those write-phase keys directly. Both expectations come from
`DriverRunner.KeysByLanguage` — the harness's own accounting of what the write phase produced —
never from the read phase being judged.

`SearchSimilar` and `SearchChunks` are served from Qdrant, which a mapped write reaches
asynchronously through the outbox — and, unlike the StarRocks projection, only after the embedding
model has vectorized every embedded and chunked field. The scenario therefore waits for both
collections before its read phase — a bounded poll with an explicit timeout, reported as a failed
step when it expires. It is never an indefinite wait and never a fixed sleep presented as
determinism; see `ProjectionWaiter.cs`.

Ranking, fused scores, result order, `topK` truncation, chunk windowing and contextual chunk
prefixes are deliberately not authored here; see the Coverage table below.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Similarity-search reachability | Covered | IVC-VEC-001 |
| Similarity-search result set | Covered | IVC-VEC-002 |
| Chunk-search reachability | Covered | IVC-VEC-003 |
| Chunk-search parent rows | Covered | IVC-VEC-004 |
| Ranking, fused scores and result order | Deferred | `SearchResponse.score` is a FUSED re-ranking score (raw cosine blended with a document-centroid similarity and a recency decay) and results are then diversified by maximal marginal relevance, so the returned order is deliberately not fused-score-descending. Both content assertions are therefore set comparisons, no assertion observes a score or a position, and no requirement constrains them. Authoring one needs a seed set whose relative similarity to a query is known independently of the server's own scoring, which is a scenario change, not a wording change. |
| `topK` truncation | Deferred | Every client's builder can express `topK`, and the scenario sends one large enough that the run's whole seeded set fits below it — which is what keeps `IVC-VEC-002`/`IVC-VEC-004` exact set comparisons rather than prefix comparisons. No assertion observes a truncation boundary and no requirement constrains one. |
| Chunk text, windowing and overlap | Deferred | `ChunkSearchResponse.chunk_text` and the `maxTokens`/`overlap` windowing are server-side properties of `IntelligenceStoreConsumer.SplitIntoChunks`, not of a client library. The scenario seeds a body short enough to produce a single window per row on purpose, so no assertion observes a window boundary and no requirement constrains one. |
| Contextual chunk prefixes and ingest enrichment | Deferred | `[IversonChunk(Contextual = true)]`, `[IversonSummary]` and `[IversonKeywords]` route through the generative enrichment path, whose output is model-dependent and not reproducible across runs. The scenario's subject type declares none of them, so no assertion observes them and no requirement constrains them. |
| Templated document chunk fields | Deferred | A chunk field named `Document` is rendered server-side from a stored document template rather than from a payload scalar. The scenario's chunk field is an ordinary payload string on purpose — which is what makes `IVC-VEC-004`'s parent set exactly the seeded rows — so no assertion observes template rendering and no requirement constrains it. |
| Vector-backed filters beyond the run marker | Deferred | `SearchSimilar` accepts the full scalar/FK filter grammar (`NOT_EQUALS`, ranges, `IN`, `OR` logic) and `SearchChunks` additionally accepts an EQUALS clause on the primary key, but the scenario sends exactly one EQUALS clause on the run marker, so no assertion observes any other operator and no requirement constrains one. |

#### Backstop assertion (non-normative)

`VEC`'s content assertions (`IVC-VEC-002`, `IVC-VEC-004`) compare what a client reported against
sets the harness itself derived from the write phase. If those sets were empty — every write denied,
or every driver silently reporting no key — an empty similarity result and an empty chunk result
would both compare equal: five clients agreeing on nothing, rendered green.
`VectorSearchScenario.Judge`'s "the run seeded at least one row for these vector queries to match"
assertion is therefore `VEC`'s backstop. It fires unconditionally, on every language, before and
outside both comparisons. Like `REL`'s, `QRY`'s and `SCH`'s it carries no requirement ID: no
`IVC-VEC-*` statement owns "the run seeded something" as such — it is a property of the harness's
own fixture, not of a client — and it is strictly weaker than `IVC-VEC-002` and `IVC-VEC-004`
wherever either can fail.

#### Known non-conformance (non-normative)

`IVC-VEC-002` fails live for the **Python, TypeScript and Go** drivers. `IVC-VEC-001`,
`IVC-VEC-003` and `IVC-VEC-004` pass for all five languages, and .NET and Java pass all four.

The cause is a client-side one, and it is the same one in all three: **they bind a result payload's
fields by PascalCase key, but `SearchSimilar` streams the raw Qdrant point payload, whose keys are
camelCase.** The lookup is an exact string match in each, so every field misses and the typed
projection comes back with nothing in it.

| Client | Read path | Key it looks for |
| --- | --- | --- |
| Python | `iverson_client/core.py:564-566` (`_entity_from_struct`) | `pascal = _to_pascal_case(field_name)`, then `if pascal in s.fields` |
| TypeScript | `src/core.ts:533-534` (`payloadToEntity`) | `toPascalCase(field)`, then `if (key in data)` |
| Go | `iverson/coordinator.go:649,671` (`fillEntityValue`) | `key := sf.Name` — a Go struct field name, so PascalCase — then `s.Fields[key]` |

The server side of the mismatch is `IntelligenceStoreConsumer.BuildObjectPointPayload`
(`Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:404-424`), which writes the row
key under the reserved literal `"key"` and every other column under
`col.Name.ToCamelCase()`; `ObjectSearchGrpcService.SearchSimilar` then streams that payload verbatim
(`Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:272`) rather than re-projecting it.

**Why the `QRY` axis never exposed this.** `Search` is served from StarRocks, not Qdrant, and returns
SQL columns spelled PascalCase — exactly what these three clients look for. `SearchSimilar` is the
first read path in the harness whose result keys are camelCase, so `IVC-QRY-002` passes for all five
languages while `IVC-VEC-002` fails for three.

.NET passes because `StructConverter`'s deserializer options set
`PropertyNameCaseInsensitive = true` (`Iverson.Clients/DotNet/Iverson.Client.Core/StructConverter.cs:15`).
Java passes because its `StructConverter` builds its field map under
`toPascalCase(f.getName()).toLowerCase()`
(`Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java:68`), which
is a case-insensitive match. Both are case-insensitive by construction; the other three are not.

The three failures render differently in the matrix purely because of each language's zero value,
which is what the assertion's label count distinguishes: Go reports **1** distinct label (five
Go-zero `""` strings), Python and TypeScript report **0** (five `None`/`undefined`, serialized to
JSON `null` and skipped). All three are the same defect.

The same run is its own control: .NET and Java retrieved all five seeded rows through the identical
request, so the server demonstrably returned them and the fault is entirely on the client side of
the wire.

Fixing this is a separate cross-client initiative touching three shipped SDKs' struct-binding code —
the same way `IVC-LIFE-007`'s gap was handled, recorded first and fixed later. It is out of scope for
this document, which records the gap as a known non-conformance rather than weakening
`IVC-VEC-002`'s statement, retiring it, or letting the harness report the affected cells as anything
other than red.

### SCH — Schema

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-SCH-001 | Active | Capability | Schema-catalogue retrieval is reachable through the client's public API |
| IVC-SCH-002 | Active | Behaviour | The catalogue a client retrieves includes the type that client registered |
| IVC-SCH-003 | Active | Behaviour | A catalogue type carries exactly the field set its registered descriptor declared |

`IVC-SCH-001` is the reachability half and `IVC-SCH-002`/`IVC-SCH-003` the content half of what an
agent-facing catalogue has to deliver, split the way `LIFE` splits `IVC-LIFE-006` from
`IVC-LIFE-008` and for the same reason: a client that can reach `GetSchema` but returns a catalogue
missing its own type, or describing that type with the wrong fields, is non-conformant in a way a
single conflated requirement would report only as one undifferentiated red cell.

`IVC-SCH-003` says *exactly* the declared field set, not *at least* it. Both directions are
verified — a declared property absent from the catalogue and a catalogued field the descriptor
never declared are each a failure. The scenario's subject type is relation-free and registers no
`FieldPermission`, which is what makes the exact form checkable: `SchemaBuilder` maps the key
property to the key column and every other declared property to a scalar column, and
`ObjectMappingGrpcService.GetSchema` emits precisely key + scalars when no field permission narrows
the set. A one-way subset reading would let a catalogue that silently dropped fields — or invented
them — satisfy the requirement.

Field-permission-narrowed catalogues, relation projection in the catalogue, and the cross-type
visibility rule (`GetSchema` drops a relation whose related type did not itself survive) are
deliberately not authored here; see the Coverage table below.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Catalogue retrieval reachability | Covered | IVC-SCH-001 |
| Registered types present in the catalogue | Covered | IVC-SCH-002 |
| Catalogue field set matches the registered descriptor | Covered | IVC-SCH-003 |
| Field-permission-narrowed catalogues | Deferred | `ObjectMappingGrpcService.GetSchema` narrows a type's fields to `AuthorizationDecision.AllowedFields` and drops a type whose authorized field set is empty, but the conformance harness registers no `FieldPermission`, so no assertion observes that narrowing and no requirement constrains it. |
| Relation projection in the catalogue | Deferred | `SchemaType.relations` is emitted (and filtered by `ForeignKeyIsReadable` and by whether the related type itself survived), but the scenario's subject type is relation-free on purpose — which is what makes `IVC-SCH-003`'s exact field-set comparison checkable — so no assertion observes relation projection and no requirement constrains it. |

#### Backstop assertion (non-normative)

`SCH`'s content assertions (`IVC-SCH-002`, `IVC-SCH-003`) search a list — the catalogue types the
driver reported — for the one type that language registered. A client that silently reported an
empty catalogue, or none at all, would make that search find nothing; `IVC-SCH-002` catches that
whenever a registered type name is known, but when the register phase itself produced no usable
descriptor there is no name to search for and neither content assertion can be evaluated at all.
`SchemaCatalogScenario.JudgeCatalogue`'s "the driver reported a non-empty schema catalogue"
assertion is therefore `SCH`'s backstop: it fires unconditionally, outside that search, on every
language and in every case. Like `REL`'s, it carries no requirement ID — no `IVC-SCH-*` statement
owns "the catalogue is non-empty" as such, and it is strictly weaker than `IVC-SCH-002` wherever
`IVC-SCH-002` can fire.

### ERR — Errors

Full rationale and the assertion(s) that discharge each requirement are recorded on the
corresponding const's doc comment in `Requirements.cs`, per this document's own convention for an
implemented requirement.

| ID | Status | Kind | Statement |
| --- | --- | --- | --- |
| IVC-ERR-001 | Active | Behaviour | A schema registration the server rejects is reported to the client as gRPC `InvalidArgument` |
| IVC-ERR-002 | Active | Behaviour | A server-side rejection's message names the element that caused it |
| IVC-ERR-003 | Active | Behaviour | A mapped write the server rejects for an invalid payload is reported to the client as gRPC `InvalidArgument` |
| IVC-ERR-004 | Active | Behaviour | A mapped read of a key with no matching row reports absence to the caller, as a completed call rather than an error status |
| IVC-ERR-005 | Active | Behaviour | A mapped write against a type the server holds no schema for is refused with gRPC `FailedPrecondition` |

**The server's error contract has two shapes, and this axis constrains both.** A schema-rule
violation is a gRPC status — the call fails, and the status code and its detail are what the caller
sees (`IVC-ERR-001`, `IVC-ERR-003`, `IVC-ERR-005`). An absent row is not: `ObjectMappingGrpcService.Get`
returns a *successful* RPC carrying `MappingResponse { Success = false, Error = "'{type}:{key}' not
found." }` (`Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`), so a client must render
that as absence through its own read API rather than as a thrown error or a blank entity
(`IVC-ERR-004`). The two shapes are authored as separate requirements because a client can get
either one right while getting the other wrong, and a single conflated requirement would report that
only as one undifferentiated red cell.

`IVC-ERR-001` and `IVC-ERR-003` are both `InvalidArgument`, and are split for the reason `QRY`
splits `IVC-QRY-001` from `IVC-QRY-003`: registration and the mapped write path are two distinct
RPCs validated by two distinct pieces of server code (`SchemaRegistrationOrchestrator.RegisterAsync`
and `RelationValidator.ValidateAndNormalizeRelations`), and a client that surfaces one correctly and
the other not is non-conformant in a way one requirement could not localize.

`IVC-ERR-002` is the message half of the same rejections whose status code `IVC-ERR-001`,
`IVC-ERR-003` and `IVC-ERR-005` grade. It is authored separately because a status code alone is not
actionable: `InvalidArgument` is the server's answer to a misnamed foreign key, a
navigation-property write, and a `PropertyName`/`ForeignKey` collision alike, and only the detail
text tells them apart. Every assertion citing it matches the specific element the fixture made
wrong — the misnamed member, the quoted navigation property, the required foreign key, the
unregistered type name — never merely that some text came back. Its `IVC-ERR-005` citation is the
one carried through all five client libraries rather than the orchestrator's own channel, which is
what makes it evidence that each library preserves the server's status detail on the way to the
caller instead of discarding it.

Status codes are reported and compared as the numeric gRPC code, never as a name — the five
languages spell the same code five ways (`FailedPrecondition`, `FAILED_PRECONDITION`, `9`), so a
name-based comparison would report a cross-language spelling difference as a conformance failure.
This is the same rule `IDN` applies to `PermissionDenied`.

**`IVC-ERR-004` is framed as an observable property of the operation, not as a return shape**, and
the five clients genuinely differ here. .NET, Python, TypeScript and Java render the server's
`Success = false` envelope as a null/`None`/`undefined` result from their mapped read; Go's
`EntityCoordinator.GetMapped` turns it into a plain (non-status) Go error and returns the zero
value, which is the idiomatic Go shape for "not found". Both satisfy the requirement, because what
it constrains is that the caller learns the row is absent and that the client does not manufacture
an error *status* out of a successful RPC — not which of a language's two idioms carries that news.
Wording it as "returns null" would have failed Go for its language's conventions, which is exactly
what `IVC-LIFE-007` was retired for. What the requirement still catches, in every language, is a
client that hands back an entity for a key no row exists under, and one that raises a gRPC status
where the server sent none; both directions were verified falsifiable live.

`IVC-ERR-005`'s fixture is a type every driver declares through its own client library and no
driver, scenario or orchestrator ever registers (`ErrorUnregisteredDoc`). `RequireSchema` is the
first thing `ObjectMappingGrpcService.Post` does, before authorization or relation validation, so
the refusal is attributable to the missing schema and to nothing else.

Refusal-reason disclosure, structured error details, streaming-RPC errors and transport statuses are
deliberately not authored here; see the Coverage table below.

#### Coverage

| Area | Status | Evidence |
| --- | --- | --- |
| Registration rejection status code | Covered | IVC-ERR-001 |
| A rejection's message names the offending element | Covered | IVC-ERR-002 |
| Mapped-write payload rejection status code | Covered | IVC-ERR-003 |
| Absent-row read reported as absence | Covered | IVC-ERR-004 |
| Write against a type with no registered schema | Covered | IVC-ERR-005 |
| Telling an absent row from a denied one | Deferred | `ObjectMappingGrpcService.Get` answers a row that does not exist and a row the caller is denied with the byte-identical envelope — `Success = false`, `Error = "'{type}:{key}' not found."` — and audits the denial only in the server's own log. A client therefore cannot distinguish the two, and `IVC-ERR-004` does not claim it can. This is a deliberate server-side non-enumeration (a distinguishable answer would confirm the row's existence to a caller not allowed to read it), so closing it is not a client change and may not be desirable at all. |
| The refusal reason behind `PermissionDenied` | Deferred | `PermissionDenied` (7) is the server's answer to several distinct refusals on the mapped write path, carrying one literal `deniedMessage` (`"Not authorized to update this entity."`) into every branch of `AuthorizationFieldMasking.EnforceWriteAuthorization` and setting no trailers, so an access denial and a tenant mismatch are indistinguishable on the wire. Verified live and disclosed by the `IDN` axis, whose `IVC-IDN-003` grades that code; no `ERR` assertion observes the distinction either, and closing it needs the server to distinguish the two refusals on the wire. |
| Structured error details and trailers | Deferred | No server path on the mapped CRUD or registration RPCs attaches `google.rpc.Status` details or response trailers — every rejection carries a status code and a human-readable detail string and nothing else — so no assertion observes a structured error payload and no requirement constrains one. Authoring one is a server change, not a wording change. |
| Streaming-RPC error propagation | Deferred | `Search` and `SearchSimilar` are server-streaming RPCs, whose failures can arrive mid-stream rather than on the initial call, and the five languages surface a mid-stream status very differently. The `error-contract` scenario exercises only unary RPCs, so no assertion observes a mid-stream failure and no requirement constrains one. Authoring one needs a fixture that fails after the first message, which is a scenario change. |
| Transport-level and retryable statuses | Deferred | `Unavailable`, `DeadlineExceeded` and `Unauthenticated` are produced by the transport, the interceptors and the identity provider rather than by Iverson's own request handling, and the harness's preflight refuses to run at all unless every one of them is healthy. No assertion observes them and no requirement constrains how a client classifies a status as retryable. |

#### Backstop assertion (non-normative)

`IVC-ERR-004` grades a client for reporting absence. A client whose mapped read reported absence for
*every* key — a broken read path, a dropped acting-user header, a type never registered — would
satisfy it while proving nothing. `ErrorContractScenario.Judge`'s "the same mapped read path finds
the row this run seeded" assertion is therefore `ERR`'s backstop: a positive control over the SAME
client method, the SAME registered type and the SAME acting user as the absent-key read beside it,
differing only in which key is asked for. It fires unconditionally, on every language, before and
outside the absence assertions. Like `REL`'s, `QRY`'s, `SCH`'s, `VEC`'s and `IDN`'s it carries no
requirement ID: no `IVC-ERR-*` statement owns "a row that exists is found" as such — that is
`LIFE`'s claim, and it is a property of the harness's own fixture here — and it is strictly weaker
than `IVC-ERR-004` wherever `IVC-ERR-004` can fail.

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
