# Relation foreign-key integrity across the five clients

**Date:** 2026-08-14
**Branch:** `client-conformance-harness`
**Status:** design approved, assumptions verified

Two independent defects, both surfaced by the conformance harness's new
"foreign key survives hydration" assertion on its first live run. They share a
symptom — a foreign key missing from a read entity — and nothing else.

## Problem

### Defect 1 — `many_to_many` nav property collides with its own foreign key

The server hydrator assigns the hydrated related row(s) to
`entityStruct.Fields[relation.PropertyName]`
(`Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs:137`). When a
descriptor's `PropertyName` equals its `ForeignKey`, that assignment overwrites
the foreign key, and a depth-1 caller can no longer see it.

Python, TypeScript and Go place the relation annotation on the **foreign-key
member** and derive the navigation property name from it. All three strip a
trailing `"Id"` — but only for `many_to_one` and `one_to_one`:

| Client | Derivation |
|---|---|
| Python | `_relation_property_name`, `iverson_client/core.py:100` |
| TypeScript | `relationPropertyName`, `src/core.ts:102` |
| Go | `relationPropertyName`, `iverson/registrar.go:328` |

So within one client:

- `py_author_id` (`many_to_one`) → property `PyAuthor`, FK `PyAuthorId` — distinct, works.
- `py_tag_ids` (`many_to_many`) → property `PyTagIds`, FK `PyTagIds` — **collision, FK destroyed.**

The anti-collision mechanism already exists and is already intended. It was
never extended from `"Id"` to `"Ids"`. This is a derivation bug, not a design
disagreement about which declaration shape is canonical.

.NET and Java are unaffected because they place the annotation on a **separate
navigation member**, so `PropertyName` is correct by construction and no
derivation is involved.

### Defect 2 — Java drops every array field on read

`StructConverter.fromStruct` populates each field via
`fromValue(entry.getValue(), f.getType())`
(`Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java:73`).
`fromValue(Value, Class)` handles `STRING_VALUE`, `NUMBER_VALUE` and
`BOOL_VALUE`, then falls through to `default -> null` (`:162-180`). There is no
`LIST_VALUE` case.

`toStruct` **does** handle `Collection` (`:110`), so writes are correct and
reads are not. The blast radius is not the harness symptom: **every array-typed
field on every Java entity comes back null**, foreign keys and ordinary array
columns alike.

The untyped `fromValue(Value)` behind `fromStructAsMap` (`:152-159`) has the
same gap, so `groupBy` and `pipeline` result maps silently drop array columns
too.

## Design

### Part A — extend the strip rule to `many_to_many`

In each of the three `relationPropertyName` functions, add a `many_to_many`
branch: if the PascalCase member name ends with `"Ids"`, the navigation property
is that name minus `"Ids"`, plus `"s"`.

`py_tag_ids` → property `PyTags`, foreign key `PyTagIds`.

This matches .NET's `DotNetTags` and Java's `javaTags`, so all five clients emit
the same navigation property name for the same relation — which is what the
interop scenario compares.

Guard the suffix test on length so a member named exactly `Ids` is left alone.
Members that do not end in `"Ids"` are untouched, so Go's sample
`Articles []string` (`Go/sample/models/tag.go:7`) keeps its current behaviour.

`_infer_fk` / `inferFk` / `inferFK` are **not** touched — the foreign-key column
name is already correct in all three clients.

### Part B — add `LIST_VALUE` to Java's struct→POJO conversion

`fromValue(Value, Class)` gains a `LIST_VALUE` case. It needs the collection's
element type, which `f.getType()` erases, so `fromStruct` passes the `Field`'s
generic type rather than its raw type — the same `ParameterizedType` unwrap
`isNavigationProperty` already performs at `:138-146`.

Elements recurse through the existing typed `fromValue`, so `List<UUID>`,
`List<String>` and `List<Integer>` are handled uniformly with no new type table.
Nested lists and lists of structs stay unsupported, matching what `toStruct` can
produce for a declared array column.

The untyped `fromValue(Value)` gains a `LIST_VALUE` case producing
`List<Object>`, so `fromStructAsMap` stops dropping array columns.

### Not changed

The server, the orchestrator, the drivers and the harness assertions are all
untouched. The "foreign key survives hydration" assertion stays exactly as
written: it is correct, and it is what surfaced both defects.

## Consequence worth noting: a server rejection path goes live

`RelationValidator.ValidateAndNormalizeRelations` computes
`navIsDistinctKey` and skips the "navigation property cannot be written"
rejection whenever `PropertyName == ForeignKey`
(`Iverson.Server/Iverson.Api/Grpc/RelationValidator.cs:20-24, 47-63`).

Part A opens that gate for Python, TypeScript and Go `many_to_many`. No client
can trip it — all three derive their write payload keys from the foreign key,
never from the navigation property name (`core.py:409`, `core.ts:468`,
`coordinator.go:547`) — so this is a strict gain, and it is what the harness's
S3 `nav-property-rejected` scenario needs in order to exercise those three
clients at all.

The server accommodation itself **stays**: Java can still produce a collision if
a user annotates the foreign-key member rather than a separate navigation
member. The stale comment naming Python and TypeScript as collision producers
should be corrected to name Java.

## Testing

Part A: each client has registrar tests asserting emitted property names, and
each already pins `property_name != foreign_key` for `many_to_one`
(`Python/tests/test_schema_registrar.py:341-358`,
`TypeScript/tests/schema-registrar.test.ts:321-322`). Each gains the
`many_to_many` equivalent. No existing test asserts a `many_to_many` property
name, so none needs changing.

Part B: `StructConverterTest` exercises `tagIds` in the `toStruct` direction
only (`:110-120`). There is no `fromStruct` array test at all — that absence is
why the gap went unnoticed. Add round-trip coverage asserting a `List<UUID>`
field survives struct→POJO, and a `fromStructAsMap` case asserting an array
column arrives as a list.

Both parts must be mutation-tested: revert the production change and confirm the
new tests go red.

## Verified assumptions

Verified 2026-08-14 against `client-conformance-harness` at `53daa85`.

| # | Assumption | Evidence | Result |
|---|---|---|---|
| A1-A3 | The three `relationPropertyName` functions are the only `property_name` derivation sites | `core.py:100,280`; `core.ts:381`; `registrar.go:129,328` — one derivation and one call site each | Holds |
| A4 | The FK column is emitted as its own `PropertyDescriptor`, so it survives the rename | `registrar.go:138` emits the FK property for every non-`one_to_many` relation; `core.py:265-273` likewise | Holds |
| A5 | The write payload key comes from the FK, not `property_name` | `core.py:409` uses `_infer_fk`; `core.ts:468` uses `inferFk`; `coordinator.go:547` uses `inferFK`. Read paths likewise: `core.py:696`, `core.ts:488`, `coordinator.go:640` | Holds — renaming cannot break writes |
| A6 | The collision claim holds across all four relation kinds, not just the one written down | `many_to_one`/`one_to_one` already strip `"Id"`; `one_to_many` derives FK `{ThisType}Id` on the *related* row, so no collision (`PyAuthor.py_articles` → property `PyArticles`, FK `PyAuthorId`; `GoAuthor.GoArticles` → `GoArticles`/`GoAuthorId`) | Holds — `many_to_many` is the only colliding kind |
| A7 | Identify existing tests asserting `many_to_many` property names | `test_schema_registrar.py:341-358` and `schema-registrar.test.ts:321-322` assert `many_to_one` only; `test_schema_registrar.py:320` asserts the FK *column* `RegTagIds`, which is unchanged | Holds — no test needs updating |
| A8 | Nothing depends on `PropertyName == ForeignKey` for `many_to_many` | One dependent found: `RelationValidator.cs:20-24`. See "Consequence" above | Partial — dependent found, effect is a strict gain |
| A9 | The server accepts a `property_name` that is not a declared column | `.NET` has shipped this shape since inception; `RelationValidator` treats a distinct nav key as write-rejectable, not registration-rejectable | Holds |
| A10 | Go's sample `Articles` member is unaffected by the conditional strip | `Go/sample/models/tag.go:7` — name does not end in `"Ids"`, so the branch does not fire; its property/FK pair `Articles`/`ArticleIds` is already distinct | Holds |
| B1 | `fromStruct` is the only struct→POJO path | `EntityCoordinator.java:140,156,193,211,225,239,291` all route through it; `:255,279` use `fromStructAsMap`. Both are in scope | Holds |
| B3 | Java entity array fields are `Collection`/`List<T>`, not Java arrays | `StructConverterTest.java:53` `List<UUID> tagIds`; `JavaArticle.java:35` `List<UUID> javaTagIds` | Holds — `List`-only handling is sufficient |
| B4 | `toStruct` emits `LIST_VALUE` for `Collection`, making the gap one-directional | `StructConverter.java:110-115` | Holds |
| B5 | `fromStructAsMap` callers tolerate `List<Object>` values | `EntityCoordinator.java:250-258, 274-282` return `List<Map<String, Object>>` to the caller untouched; values were previously `null` | Holds — change is additive |
| B6 | No existing Java test asserts null/absent for an array field on read | `StructConverterTest.java` covers `tagIds` only via `getFieldsOrThrow("TagIds")` on the write side | Holds — and the absence is the root cause |
| C1 | .NET shares none of the changed derivation code | `SchemaRegistrar.cs:85` uses `relation.Property.Name` directly; `InferForeignKey` at `:268` is separate | Holds |

## Out of scope

Java's array-read gap means the `array-column-mapping` initiative's Java arm was
never working on the read path, and its suite went green regardless. Part B
fixes the mechanism. Whether that initiative's Java coverage needs a wider
re-audit is a separate decision.

A Java or .NET user who annotates the foreign-key member rather than a separate
navigation member still produces a colliding descriptor, because neither client
derives a navigation name. Neither client documents that shape.
