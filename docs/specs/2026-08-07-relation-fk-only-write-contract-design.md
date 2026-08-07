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

| Client | Where the relation is declared | FK reaches the server? | Nav property sent? |
|---|---|---|---|
| Go | On the FK-bearing field itself | **Never** — the serializer skips every relation-tagged field | none declared |
| Python | On the FK-bearing field | Yes | `articles` list → `str(value)` junk |
| TypeScript | Decorator on the FK field; the serializer ignores decorators | Yes, by naming convention | yes, raw |
| Java | Separate nav member; **no FK field for ManyToMany** | Scalars yes, m2m never | yes; collections hit a `toString()` fallback |
| .NET | Separate nav member plus a plain FK field | Yes, except on `Tag` | yes, whole object graph |

### The Go defect

`Article.AuthorId` is declared `` `iverson:"many_to_one:Author"` `` and
`registrar.go:266-269` confirms the field name *is* the FK column. But
`coordinator.go:440-447` skips every relation-tagged field before serializing,
`KindManyToOne` included. Every entity the Go client has ever written carries a **null
foreign key**, silently — the field is simply absent from the payload, so the server has
nothing to reject. This affects ManyToOne and OneToOne, not only ManyToMany.

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

## Clients

The distinguishing question is not what the relation-marked member is *called* but what it
*holds*. A name-based test is unsafe: TypeScript and Python infer a ManyToOne FK as
`{RelatedType}Id`, so a field named `writerId` for an `Author` relation would be
misclassified as a nav property and its FK silently dropped.

**Marked member holds ids** (string, UUID, or an array of those) — it *is* the foreign key.
Serialize its value under the **inferred FK column name**, not the field's own name. For
Python's `author_id` and TypeScript's `authorId` the inferred name equals what they send
today, so nothing changes on the wire; for Go's `Articles []string` it is `ArticleIds`.

**Marked member holds entities** (an entity type or a collection of them) — it is a
navigation property. Omit it entirely. The FK comes from the separate plain field beside
it, which carries no relation marker and serializes normally.

OneToMany needs no special case under this rule *except in Go* (below): its marked member
holds entities in .NET, Java, Python and TypeScript, so it is always omitted.

### Per-client work

**Go** — `entityToStruct` stops discarding relation-tagged fields. ManyToOne and OneToOne
serialize under their own name (`inferFK` already returns `fm.Name`). ManyToMany serializes
under `inferFK`'s `{RelatedType}Ids`. **OneToMany is excluded explicitly**: `inferFK`
returns `{ThisType}Id` for that kind, which names a column on the *related* entity's row,
and `Author.Articles []string` is a real declaration that would otherwise emit under
`AuthorId`. A Go relation field holding structs is omitted as a nav property; none exist
today, but the rule should not depend on that.

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

**Python** — `_entity_to_struct` applies the FK-name mapping and omits entity-valued
relation members. It already reads `_iverson_meta` (`core.py:314`), and `core.py:166`
establishes the precedent of building a relation-field set from it.

**TypeScript** — `entityToPayload` does the same. `getRelations` is already imported
(`core.ts:59`).

## Consequences

**Go writes change behavior.** Writes that silently persisted a null foreign key will
persist the real one. Data written by the Go client to date has null FKs that this change
does not backfill.

**Two entities become StarRocks-eligible.** `StoreTargeting.IsEngagementEligible` marks a
ManyToMany eligible only when a declared FK column matches its foreign key, and
`SchemaBuilder.cs:110` registers an FK column only when a scalar property's name equals it.
Neither .NET `Tag` nor Java `Article` has one today, so both are ineligible. Adding the FK
fields makes them eligible and they will begin projecting into the engagement store.
**Ben accepted this on 2026-08-07 as the correct outcome** — a many-to-many whose ids live
in a real column should be projectable.

**Server and clients must ship together.** Rejection is not backward-compatible: any
caller still sending a nav property starts failing hard.

## Testing

`RelationValidatorTests` currently holds 34 tests dominated by embedded-object scenarios;
roughly eighteen are deleted rather than adapted, replaced by per-kind rejection tests plus
the retained FK-validation cases.

Each client needs a serialization test asserting that a written payload contains the FK key
and does **not** contain the nav key. Go needs one asserting `AuthorId` is present at all —
the defect that motivated this work has no existing coverage.

## Verified assumptions

Twenty-six assumptions were enumerated against the design before any verification, then
checked against the codebase. Twenty-three held; three are recorded below.

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
| A22 | The server enforces `{RelatedTypeName}Id` FK naming | ❌ **FAILED** — nothing validates that a relation's `ForeignKey` names a real column. The only detector is the embedded path's `fkCol is null` error at `RelationValidator.cs:106-111`, which this design deletes. See Known issues |
| A23 | The read path re-fetches relations and never reads a stored nav property | ✅ `EntityRelationResolver.cs:96,137,176` write into fresh results |
| A24 | No client test asserts nav properties are sent in write payloads | ✅ no payload-content assertions in the client suites |
| A25 | The four relation kinds are exhaustive in every client | ✅ |
| A26 | LoadTest entities are unaffected | ✅ `BenchmarkArticle` has both `BenchmarkAuthorId` and a nav property; inferred name matches |

## Known issues / accepted as out of scope

- **A misdeclared relation loses its only detector (A22).** After the embedded path is
  deleted, a relation whose `ForeignKey` names no column surfaces as a misleading
  "relation is required" error rather than naming the real cause. Adding registration-time
  validation would be the real fix and is deliberately not in scope.
- **Go's historical null foreign keys are not backfilled.**
- **Python `str()` serialization generally.** This design omits entity-valued relation
  members, which removes the observed junk, but Python's converter still falls through to
  `str(value)` for any other unhandled type.
- **Client-side bandwidth for non-relation fields** is untouched.
