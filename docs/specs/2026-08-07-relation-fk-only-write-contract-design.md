# Foreign-key-only relation write contract

**Date:** 2026-08-07
**Status:** Design approved, not yet planned
**Supersedes parts of:** `2026-08-05-relation-properties-write-path-design.md`,
`2026-08-06-strip-relation-nav-properties-design.md`

## Problem

Server-side stripping of relation navigation properties shipped on 2026-08-07
(local `main@70ee344`). It keeps nav properties out of Postgres, StarRocks, Qdrant and the
Kafka event body, but it does not stop clients sending them, and it masked a set of
client-side defects rather than fixing them.

Investigating what remained found the five clients disagree on almost everything about
relation serialization, including one client that cannot write a foreign key at all.

| Client | Where the relation is declared | FK persists? | Nav property sent? |
|---|---|---|---|
| Go | On the FK-bearing field itself | **Never** — the serializer skips every relation-tagged field, and no FK column is declared | none declared |
| Python | On the FK-bearing field | **Never** — reaches the payload, but no FK column is declared to hold it | `articles` list → `str(value)` junk |
| TypeScript | Decorator on the FK field; the serializer ignores decorators | **Never** — same as Python | yes, raw |
| Java | Separate nav member; **no FK field for ManyToMany** | Scalars yes, m2m never | yes; collections hit a `toString()` fallback |
| .NET | Separate nav member plus a plain FK field | Yes, except on `Tag` | yes, whole object graph |

### The Go defect

`Article.AuthorId` is declared `` `iverson:"many_to_one:Author"` `` and
`registrar.go:266-269` confirms the field name *is* the FK column. But
`coordinator.go:440-447` skips every relation-tagged field before serializing,
`KindManyToOne` included. Every entity the Go client has ever written carries a **null
foreign key**, silently — the field is simply absent from the payload, so the server has
nothing to reject. This affects ManyToOne and OneToOne, not only ManyToMany.

### The undeclared foreign-key column

All three clients that mark the FK-bearing field exclude that field from the properties they
register: Go at `tags.go:315-325` (*"Relations never reach `meta.Fields`"*, and
`registrar.go:64` builds properties from `meta.Fields`), Python at `core.py:187-188`, and
TypeScript at `core.ts:238`. `SchemaBuilder.cs:56` builds `ScalarColumns` from properties, so
no `AuthorId` column exists in Postgres or StarRocks for any entity these three register.

This is why fixing the Go serializer alone is not enough: the value would reach the payload
under a key with no column behind it, and the stores ignore unknown keys. Python and
TypeScript have the same defect by a different mechanism — their foreign keys have always
reached the payload and never landed anywhere. .NET and Java are unaffected: their FK fields
are plain properties carrying no relation marker, so they are declared normally.

### The declaration-model split

Go, Python and TypeScript put the relation marker **on the FK-bearing field**. .NET and
Java declare a **separate nav member** alongside a plain FK field. There is no single
"strip the nav property" rule that is correct for both: in Go, stripping the
relation-tagged field *is* the bug.

## Contract

A write payload carries **foreign keys only**.

| Relation kind | FK-bearing payload key |
|---|---|
| ManyToOne, OneToOne | `{RelatedType}Id` |
| ManyToMany | `{RelatedType}Ids` — a list of id strings |
| OneToMany | **none** — the FK lives on the related entity's row |

A navigation property must not appear in a write payload. Where the relation marker sits
*on* the FK-bearing member, that member is not a nav property and is sent normally.

Reads are unaffected. `EntityRelationResolver` and the client `GraphAssembler`s keep
hydrating nav properties on the way back; this contract governs writes only.

Embedded-object writes are retired. A nav property in a write payload is a caller error
and is rejected with `InvalidArgument`.

## Server

`RelationValidator` collapses rather than grows. Everything that exists to interpret a
nested object is deleted: `ValidateNestedObject` (the key-only rule and the
cascade-insert error), `ReadNestedKey`, `KeyColumnNameFor`, the normalize branches in both
per-kind methods, and the FK/nav conflict detection added on 2026-08-06 — moot once a nav
property cannot legally be present. Removing `KeyColumnNameFor` drops the class's
`SchemaRegistry` dependency, so the constructor becomes parameterless.

The capture/restore machinery goes with it. Nothing is stripped, so there is nothing to
restore: `CaptureNavProperties`, `RestoreNavProperties` and
`StructFieldAccess.RemoveField` all become dead and are deleted.

What remains, per relation:

1. If the nav property key is present and distinct from the FK, record
   `Relation '<Name>' is a navigation property and cannot be written — send '<ForeignKey>' instead.`
   A `NullValue` nav key **counts as absent** and is tolerated, matching the foreign-key rule at
   `RelationValidator.cs:78-81` — the same fact drives both: .NET and Java serialize every
   property, so an unset nav member arrives as `Author: null`. A caller who meant to send an
   embedded object but produced a null therefore gets no diagnostic; that is accepted in exchange
   for one consistent null rule across every payload key.
2. Validate the FK: GUID well-formedness and non-emptiness for singles, per-element for
   ManyToMany lists.
3. Keep the required-relation check for a non-nullable FK column. Its message loses the
   `or '<Name>' (embedded object)` half.

Errors continue to accumulate into one `InvalidArgument` rather than short-circuiting.

### Ordering constraint

Field authorization runs **before** the validator (`ObjectMappingGrpcService.cs:294` vs
`:299`) and throws on the first disallowed field. The carve-out at
`RowFieldAuthorizationEvaluator.cs:92-95`, which admits relation property names on write
when the FK is not excluded, **must stay** — without it a caller sending a nav property
gets an opaque authorization error instead of the message above.

Its *rationale* changes, and the code comment must be rewritten to match. Today the
carve-out exists because sending `Author` was equivalent to writing `AuthorId` (the
validator normalized one into the other after the check ran), so one permission had to
govern both. Under this design nothing is normalized; the carve-out exists solely so the
rejection is legible. A caller whose `AuthorId` is excluded still fails at authorization,
which remains correct.

## Registration

Serialization and registration must follow the same rule, or the payload key and the column
name diverge and the write is silently discarded. Both are stated in terms of the same
inferred FK name.

**Declaration.** In the clients that mark the FK-bearing field (Go, Python, TypeScript), a
`ManyToOne` / `OneToOne` / `ManyToMany` relation declares an FK property in addition to
emitting the relation descriptor. Each client appends that property **after** its existing
field loop, built from the relation descriptor — named by the inferred FK column name, typed
as a string id (an array of them for ManyToMany), and omitted entirely for `OneToMany`, whose
foreign key is a column on the related type's row. The existing exclusions stay
**unconditional**: Go's also enforces that a tenant marker on a relation is not a tenant
declaration (`tags.go:316-318`), and TypeScript's field loop rejects any array property
lacking `@IversonArray` (`core.ts:242-250`), which a reflected ManyToMany FK would trip.
Synthesizing from the descriptor also supplies the element type directly and gets the m2m
name right, which a field loop would not — it names properties after the field. .NET and Java
need no change; their FK fields are already plain declared properties.

**Validation.** `SchemaRegistrationOrchestrator` gains a check, alongside the existing
`owner_field` and `tenant_field` checks, that every `ManyToOne` / `OneToOne` / `ManyToMany`
relation's `ForeignKey` matches a declared column, rejecting with `InvalidArgument`
otherwise. `OneToMany` is exempt for the same reason it declares nothing.

This cannot reuse `ValidateFieldReference`: that helper additionally requires a string-valued
`SqlType` for Qdrant filtering (`SchemaRegistrationOrchestrator.cs:109-115`), which a
ManyToMany's `UUID[]` foreign key is not. The new check tests membership only.

The validation is what keeps the two rules from drifting apart again — it fails registration
at the point where a client declares a relation whose foreign key nothing can hold.

## Clients

The classification is by **relation kind first**, not by member type. A type-based test is
not decidable in Python or TypeScript — TypeScript erases types, and Python's annotations
carry no element type, so an empty collection is indistinguishable from an empty id list. A
name-based test is equally unsafe: a field named `writerId` for an `Author` relation would
be misclassified and its FK silently dropped.

**`OneToMany` — always omitted, in every client.** Its foreign key lives on the related
entity's row, so it contributes no key to this payload under any declaration style. This is
the only case where Python's and TypeScript's ambiguity would otherwise be reachable.

**`ManyToOne` / `OneToOne` / `ManyToMany`** — in the clients that mark the FK-bearing member
(Go, Python, TypeScript) the marked member *is* the foreign key by construction; serialize
its value under the **inferred FK column name**, with no type test. For Python's `author_id`
and TypeScript's `authorId` the inferred name equals what they send today; for Go's
`Articles []string` it is `ArticleIds`.

In .NET and Java, where a nav member and an FK field genuinely coexist, the declared type is
available at the decision point and distinguishes them: a member typed as an entity or a
collection of entities is a navigation property and is omitted; the FK comes from the
separate plain field beside it, which carries no relation marker and serializes normally.

The relation kind is present at every decision point: `meta["relations"]` carries `kind`
(`core.py:166`), TypeScript's `getRelations()` returns `RelationMeta.kind`, and Go's
`ParseTag` yields `fm.RelationKind`.

### Per-client work

**Go** — `entityToStruct` stops discarding relation-tagged fields. ManyToOne and OneToOne
serialize under their own name (`inferFK` already returns `fm.Name`). ManyToMany serializes
under `inferFK`'s `{RelatedType}Ids`. **OneToMany is excluded explicitly**: `inferFK`
returns `{ThisType}Id` for that kind, which names a column on the *related* entity's row,
and `Author.Articles []string` is a real declaration that would otherwise emit under
`AuthorId`. A Go relation field holding structs is omitted as a nav property; none exist
today, but the rule should not depend on that. `registrar.go` additionally appends the
synthesized FK property per Registration — without it the newly-emitted `AuthorId` still has
no column. `tags.go`'s `meta.Fields`/`meta.Relations` split is left alone.

**Java** — three changes. Entities with a `@ManyToMany` gain a plain `List<UUID> tagIds`
beside the nav collection. `toStruct` skips members carrying a relation annotation whose
type is an entity or collection of entities. `toValue`'s `toString()` fallback
(`StructConverter.java:102`) gains a `Collection` branch emitting a proper `ListValue` of
recursively-converted elements; the fallback stays for genuinely unknown types.

**.NET** — `Tag` gains `Guid[] ArticleIds` (see Verified assumptions, A11).
`StructConverter.ToStruct` is a blind JSON round-trip, so the four write call sites in
`EntityCoordinator` supply the descriptor's nav-property names for omission;
`_descriptor` is already in scope. `GraphAssembler` also calls `ToStruct` (`:95`, `:209`)
on the read path — those callers read FK values only and are unaffected, but the signature
change must keep them compiling.

**Python** — `_entity_to_struct` omits `OneToMany` members and maps the rest through the
inferred FK name. It gains a list branch emitting a `ListValue` of recursively-converted
elements — without it a ManyToMany id list reaches the `str(value)` fallback (`core.py:341`)
and arrives as a string, which the validator's `fkValue?.ListValue` read silently ignores.
This is the same fix as Java's `Collection` branch. It already reads `_iverson_meta`
(`core.py:314`), and `core.py:166` establishes the precedent of building a relation-field
set from it. It appends the synthesized FK property per Registration; the exclusion at
`core.py:187-188` is unchanged.

**TypeScript** — `entityToPayload` does the same. `getRelations` is already imported
(`core.ts:59`). It appends the synthesized FK property per Registration; the exclusion at
`core.ts:238` is unchanged.

## Consequences

**Go, Python and TypeScript writes change behavior.** All three begin persisting foreign
keys that previously went nowhere — Go's were never serialized, Python's and TypeScript's
were serialized into a column that did not exist. Data written by any of the three to date
has null FKs that this change does not backfill.

**Registration becomes stricter and adds a column.** The three clients' entities gain an FK
column they did not previously declare, which is a schema change applied through the existing
`ApplySchemaAsync` drift path. A relation whose foreign key matches no declared column now
fails registration outright rather than registering and silently discarding writes.

**Two entities become StarRocks-eligible.** `StoreTargeting.IsEngagementEligible` marks a
ManyToMany eligible only when a declared FK column matches its foreign key, and
`SchemaBuilder.cs:110` registers an FK column only when a scalar property's name equals it.
Neither .NET `Tag` nor Java `Article` has one today, so both are ineligible. Adding the FK
fields makes them eligible and they will begin projecting into the engagement store.
**Ben accepted this on 2026-08-07 as the correct outcome** — a many-to-many whose ids live
in a real column should be projectable. The Registration change extends the same effect to
Go, Python and TypeScript entities, whose ManyToMany foreign keys also become declared
columns for the first time.

**Server and clients must ship together.** Rejection is not backward-compatible: any
caller still sending a nav property starts failing hard.

## Testing

`RelationValidatorTests` currently holds 34 tests dominated by embedded-object scenarios;
roughly eighteen are deleted rather than adapted, replaced by per-kind rejection tests plus
the retained FK-validation cases.

`ObjectMappingGrpcServiceTests.cs:725` and `:753` assert the echoed response retains the nav
property, and `AuthorizationFieldMaskingTests.cs` references nav property names. The first
two are deleted with the capture/restore machinery; the third needs a read.

Each client needs a serialization test asserting that a written payload contains the FK key
and does **not** contain the nav key. Go needs one asserting `AuthorId` is present at all —
the defect that motivated this work has no existing coverage.

Go, Python and TypeScript each need a registration test asserting the FK column is declared
under the inferred name for the three non-OneToMany kinds, and absent for `OneToMany`. The
server needs one per kind for the new registration check — rejected when the foreign key
matches no column, accepted for `OneToMany`, and accepted for a ManyToMany whose FK column is
an array type (the case `ValidateFieldReference` would have wrongly rejected).

## Verified assumptions

Thirty-two assumptions, enumerated against the design before verification and checked against
the codebase. Twenty-four held; the rest are recorded below. A27–A32 were added across three
review rounds and the A22 fix; A29 is the one that changed the design most.

| # | Assumption | Result |
|---|---|---|
| A1 | Removing the nested-object helpers drops `RelationValidator`'s `SchemaRegistry` dependency | ✅ `registry` used only at `:210` |
| A2 | `StructFieldAccess.RemoveField` has no consumer outside the strip | ✅ sole caller `RelationValidator.cs:51` |
| A3 | `Capture`/`RestoreNavProperties` are used only for the strip/restore round-trip | ✅ `ObjectMappingGrpcService.cs:298,309,353,357` |
| A4 | Field auth runs before the validator; the carve-out admits relation names | ✅ `:294` before `:299`; `RowFieldAuthorizationEvaluator.cs:92-95` |
| A5 | All write paths route through `ValidateAndNormalizeRelations` | ✅ exactly 4 sites |
| A6 | No other server component reads a nav property from a write payload | ✅ consumer `PropertyName` hits are vector/chunk fields |
| A7 | `StructFieldAccess.Candidates` survives the deletion | ✅ kept alive by `EntityKeyAccessor.cs:15,23` |
| A8 | `RelationValidatorTests` is dominated by embedded-object cases | ✅ 34 tests, ~18 embedded-specific |
| A9 | `EntityDescriptor` exposes `Relations`; `_descriptor` is in scope at all write sites | ✅ `EntityDescriptor.cs:10` |
| A10 | `ToStruct` has no other callers | ⚠️ **PARTIAL** — also `GraphAssembler.cs:95,209` on the read path; both read FK values only |
| A11 | .NET sample entities carry a separate FK field for every relation | ❌ **FAILED** — `Tag.cs:17-18` declares `[ManyToMany] List<Article> Articles` with a comment claiming `ArticleIds` and **no such property**. Design updated: `Tag` gains `Guid[] ArticleIds`, making the fix a rule for both nav-property clients rather than Java-specific |
| A12 | Java may already declare a `TagIds` FK column | ✅ it does not — `SchemaRegistrar` collects relation fields as `navFieldNames` and excludes them; Java `Article` is StarRocks-ineligible today |
| A13 | Java `toStruct` can read relation annotations at serialization time | ✅ iterates `getAllFields`; `isRelationField` exists in `SchemaRegistrar` |
| A14 | `toValue`'s `toString()` fallback is the only path collections take | ✅ no `Collection` branch exists |
| A15 | Python `_entity_to_struct` can reach relation metadata | ✅ already reads `_iverson_meta` at `core.py:314` |
| A16 | Python's `one_to_many` field serializes as `str()` junk | ✅ `Author.articles` falls to the `str(value)` branch |
| A17 | TypeScript `entityToPayload` can reach `getRelations()` | ✅ already imported at `core.ts:59` |
| A18 | Python/TS inferred ManyToOne FK equals the field's PascalCase name | ✅ `author_id`/`authorId` → `AuthorId`; no wire change |
| A19 | Go `entityToStruct` is the sole write serializer and can reach `inferFK` | ✅ same package |
| A20 | Go declares no struct-typed relation fields | ✅ all are `string`/`[]string` |
| A21 | Whether Go `one_to_many` is declared anywhere | ✅ it is — `author.go:8` `Articles []string`. The OneToMany exclusion is load-bearing, not hypothetical |
| A22 | The server enforces `{RelatedTypeName}Id` FK naming | ❌ **FAILED** — nothing validates that a relation's `ForeignKey` names a real column. The only detector was the embedded path's `fkCol is null` error at `RelationValidator.cs:106-111`, which this design deletes. **Now addressed by the Registration section** rather than deferred; investigating the fix is what surfaced A29 |
| A23 | The read path re-fetches relations and never reads a stored nav property | ✅ `EntityRelationResolver.cs:96,137,176` write into fresh results |
| A24 | No client test asserts nav properties are sent in write payloads | ✅ no payload-content assertions in the client suites |
| A25 | The four relation kinds are exhaustive in every client | ✅ |
| A26 | LoadTest entities are unaffected | ✅ `BenchmarkArticle` has both `BenchmarkAuthorId` and a nav property; inferred name matches |
| A27 | No test outside `RelationValidatorTests` depends on capture/restore | ❌ **FAILED** — `ObjectMappingGrpcServiceTests.cs:725,753` pin the echoed-payload behavior this design makes unreachable; both are deleted, not adapted |
| A28 | Every client can serialize the FK value type it is required to emit | ❌ **FAILED for Python** — Go's `goValueToProtoValue` handles `[]string`; TS assigns arrays raw and the proto layer yields `ListValue`; .NET's `Guid[]` round-trips through `JsonParser`; Java gains the `Collection` branch this design specifies. Python's `_entity_to_struct` has no list branch (`core.py:330-341`) — see the Python per-client fix |
| A29 | The FK-on-field clients declare the FK-bearing field as a property | ❌ **FAILED — all three** — Go `tags.go:315-325` + `registrar.go:64`, Python `core.py:187-188`, TypeScript `core.ts:238` all exclude relation-marked fields from the registered properties, and `SchemaBuilder.cs:56` builds `ScalarColumns` from properties. No FK column has ever existed for these clients, so the Go serializer fix alone would not persist anything. Design updated: see Registration |
| A30 | The registration check can reuse `ValidateFieldReference` | ❌ **FAILED** — it additionally requires a string-valued `SqlType` for Qdrant filtering (`SchemaRegistrationOrchestrator.cs:109-115`), which rejects a ManyToMany's `UUID[]` foreign key. The new check tests column membership only |
| A31 | `SchemaRegistrationOrchestrator` is the right place, with an established pattern | ✅ `:53-66` already performs `ValidateEnrichmentTargets`, `owner_field` and mandatory `tenant_field` checks, all throwing `RpcException(InvalidArgument)` |
| A32 | The FK-bearing field can safely enter each client's property loop | ❌ **FAILED for Go and TypeScript** — Go's exclusion also gates the tenant-declaration check (`tags.go:316-323`); TypeScript's loop throws on any array property lacking `@IversonArray` (`core.ts:242-250`), which a ManyToMany FK necessarily is. Python's loop is safe (`_python_type_to_clr` handles `list[str]`). Design updated: the FK property is synthesized from the relation descriptor instead |

## Known issues / accepted as out of scope

- **Historical null foreign keys are not backfilled** for Go, Python or TypeScript.
- **Python `str()` serialization for non-relation types.** The list branch above covers
  relation id lists; Python's converter still falls through to `str(value)` for any other
  unhandled type.
- **Client-side bandwidth for non-relation fields** is untouched.
