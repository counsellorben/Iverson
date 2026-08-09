# UUID key and foreign-key column typing

**Date:** 2026-08-09
**Status:** Design approved, not yet planned
**Related:** `2026-08-07-relation-fk-only-write-contract-design.md` (introduces the synthesized
foreign-key property this spec retypes), `2026-08-09-client-conformance-harness-design.md`
(depends on this fix to pass its Go and TypeScript scenarios)

## Problem

The server requires entity keys and relation foreign keys to be UUIDs. It does not enforce that at
registration, and three of five clients cannot declare it. The result is schemas that register
successfully, accept writes, and then fail at read time with a Postgres type error.

Two distinct breakages, both confirmed against a running stack on 2026-08-09.

### Text key columns cannot be read

`EntityRepository` hardcodes a uuid cast in every key predicate:

```csharp
WHERE "{schema.KeyColumn.Name}" = @Key::uuid   // FetchByKeyAsync :9, FetchByColumnAsync :26,
                                               // DeleteAsync :39, UpdateAsync :64
var keyGuids = keys.Select(Guid.Parse).ToArray();   // FetchManyByKeysAsync :16
```

A key declared `CLR_STRING` becomes a `TEXT` column (`SchemaBuilder.ScalarTypeMap`), and every one
of those operations then throws `42883: operator does not exist: text = uuid`. Verified live: a
text-keyed type accepted a write, then failed **both** `depth=0` and `depth=1` reads.

Three clients declare string keys in their sample models — Go `Id string`, TypeScript
`id: string`, Python `id: str` — so this is the default experience for those clients, not an edge
case. .NET (`Guid`) and Java (`UUID`) are unaffected.

Go and TypeScript are additionally unable to declare otherwise: Go's `goScalarToClr` maps
`reflect.String → CLR_STRING` with no UUID case, and TypeScript's `jsTypeToClr` has no Guid case
and defaults to `CLR_STRING`.

### Text foreign-key columns break one-to-many resolution

The foreign-key-only work (2026-08-07) has Go, Python and TypeScript synthesize their relation
foreign-key property as `CLR_STRING`, producing a `TEXT` column. `EntityRelationResolver:154`
resolves a `OneToMany` by calling `FetchByColumnAsync`, which casts the same way — so the reverse
lookup compares a TEXT column against a uuid parameter and throws.

.NET and Java are unaffected: their foreign key is a separate declared field typed `Guid`/`UUID`.

The many-to-one direction is unaffected in all five clients, because it casts the foreign-key
*value* and compares it against the related table's uuid *key* column. That asymmetry is why the
defect survived the branch's review: the many-to-one path was exercised live and passed.

Verified live: `fk_t_articles` shows `FkTAuthorId | text`, and a `depth=1` read of the author
throws `42883`.

## Contract

**A key column and a relation foreign-key column are UUID.** This is already the system's operating
assumption — `RelationValidator` rejects a foreign key that is not a well-formed non-empty GUID
(`:88`, `:110`), `IntelligenceStoreConsumer.KeyToUlong` documents that "keys are server-generated
UUIDv7", and every Postgres key predicate casts accordingly. This spec makes the assumption
enforceable and declarable rather than implicit.

Registration rejects a violation. Every client can express conformance.

### Alternative considered and rejected

Making the SQL cast conditional on the column's declared type would let TEXT keys work. It was
rejected because it only half-supports them: `RelationValidator` requires foreign-key values to be
GUIDs, so a TEXT-keyed entity could never be the target of a relation. That produces a type that
works alone and fails on being related to — a sharper edge than the one being removed. **Ben chose
enforcement on 2026-08-09.**

## Server

`SchemaRegistrationOrchestrator` gains one check, beside the existing `owner_field` and
`tenant_field` validations (`:53-66`) and the foreign-key-column check added by the FK-only work:

- the key column's SQL type must be `UUID`
- a `ManyToOne` or `OneToOne` relation's foreign-key column's SQL type must be `UUID`
- a `ManyToMany` relation's foreign-key column's SQL type must be `UUID[]` — the array form
  `ClrTypeToSql` produces for `CLR_GUID`

Only the `OneToMany` reverse lookup compares a foreign-key column in SQL. Many-to-many resolution
reads the id list from the payload and matches the related type's *key* via `= ANY(@Keys)`, so the
`UUID[]` requirement is a consistency rule rather than one a read path depends on.

Both reject with `InvalidArgument`, naming the type, the offending field, its declared type, and
the required one. The message must be actionable by a client developer who has never read the
server source — it is the only signal they will get.

`OneToMany` is exempt from the foreign-key half for the same reason it declares no column: its
foreign key lives on the related entity's row and is validated when *that* type registers.

No change to `EntityRepository`. The casts become correct by construction once the guard holds.

## Clients

**Go** gains a struct tag marking a property as UUID-typed, in the existing tag vocabulary
(`iverson_key:"true"`, `iverson_tenant:"true"`):

```go
Id string `iverson_key:"true" iverson_guid:"true"`
```

A tag rather than a Go type, because the client has no UUID dependency today (`go.mod` is grpc and
protobuf only) and adding one to express a column type would be a heavy way to carry one bit.

**TypeScript** gains an `@IversonGuid()` property decorator:

```typescript
@IversonKey()
@IversonGuid()
id: string = '';
```

This mirrors `@IversonArray(elementType)`, which exists for exactly this reason — TypeScript erases
types, so a decorator is the only way to say something the runtime cannot observe. The property
loop consults it before falling back to `jsTypeToClr`.

**Python** needs no new mechanism: `_PY_TO_CLR` already maps the `uuid`/`UUID` annotation to
`CLR_GUID`. Its sample models declare `id: str` and must change to `id: uuid.UUID`.

**All three** change their synthesized relation foreign-key property from `CLR_STRING` to
`CLR_GUID`. This is the one-to-many fix and is not optional — with the guard in place, a schema
carrying a `CLR_STRING` foreign key fails registration.

**.NET and Java** need no change.

### Samples

The Go, TypeScript and Python sample models declare GUID keys. These are the live-usable examples;
leaving them as-is would ship three clients whose own samples fail to register.

Client **test fixtures are out of scope**. They mock the transport, so a server-side guard never
sees them, and they continue to pass unchanged. Only fixtures asserting the emitted `clr_type` of a
synthesized foreign key need updating, and only because that value changes.

## Testing

The server needs registration tests per rejection: a non-UUID key column is rejected; a non-UUID
foreign-key column is rejected; a `OneToMany` whose foreign key is absent is accepted; a
well-formed schema is accepted. Each must assert the message names the offending field.

Go and TypeScript each need a registration test asserting the new tag/decorator yields `CLR_GUID`,
and that its absence still yields `CLR_STRING` for a non-key property.

Go, Python and TypeScript each need a test asserting the synthesized foreign-key property is
`CLR_GUID`.

Every test must be shown to fail against the unfixed code. The defects in this spec were both
invisible to five green suites; a test that cannot fail adds nothing here.

## Consequences

**Registration becomes stricter, and this is a breaking change.** Any deployed entity with a
non-UUID key or foreign key stops re-registering until its declaration is corrected. Existing rows
are untouched — but the table cannot be re-registered, which the schema-drift path requires on any
subsequent change.

**Three clients' sample models change shape.** Anyone following them must migrate their key
declarations.

**One-to-many relations begin working for Go, Python and TypeScript** for the first time.

**Read-by-key begins working for Go and TypeScript** for the first time.

## Verified assumptions

Nine assumptions, enumerated against the design before verification, checked against the codebase
and a running stack. Two failed; both failures are the defects this spec fixes.

| # | Assumption | Result |
|---|---|---|
| B1 | Go's tag vocabulary can take a new tag without disturbing the relation/tenant split | ✅ `iverson_key`, `iverson_tenant`, `iverson_search_key` are independent tags parsed alongside `iverson` |
| B2 | TypeScript has a precedent for a decorator overriding an unobservable type | ✅ `@IversonArray(elementType)` at `annotations.ts:250`, existing because TS erases element types |
| B3 | Python's `uuid` annotation yields `CLR_GUID` | ✅ `_PY_TO_CLR` maps both `"uuid"` and `"UUID"` (`core.py:37-38`) |
| B4 | The guard's blast radius is limited to samples | ✅ **narrower than feared** — client test fixtures mock the transport, so a server-side guard never evaluates them. Only samples and `clr_type`-asserting fixtures change |
| B5 | .NET and Java samples already declare UUID keys | ✅ `Article.cs:9` `public Guid Id`; `Article.java:17` `private UUID id` |
| B6 | The guard belongs in `SchemaRegistrationOrchestrator` | ✅ `:53-66` already performs `owner_field` and mandatory `tenant_field` checks, both throwing `RpcException(InvalidArgument)` |
| B7 | The synthesized foreign-key column type is compatible with one-to-many resolution | ❌ **FAILED** — Go/Python/TS emit `CLR_STRING` → `TEXT`; `EntityRelationResolver:154` → `FetchByColumnAsync` casts `@Key::uuid`. Confirmed live: `FkTAuthorId | text`, author `depth=1` throws `42883` |
| B8 | Key columns of any declared type are readable | ❌ **FAILED** — `EntityRepository` hardcodes `@Key::uuid` in four predicates and `Guid.Parse` in a fifth. Confirmed live: a text-keyed type accepted a write, then failed `depth=0` *and* `depth=1` reads |
| B9 | A ManyToMany foreign-key column's SQL type is distinguishable from a ManyToOne's | ✅ `ClrTypeToSql(t, isArray)` (`SchemaBuilder.cs:278-281`) consults `ArrayTypeOverrides` first: `ClrGuid` + array → `UUID[]` (`:252`), scalar `ClrGuid` → `UUID` (`:236`). The guard must accept both forms |

Additionally inherited from the conformance-harness spec's verification (2026-08-09): Go's
`goScalarToClr` has no UUID case and TypeScript's `jsTypeToClr` defaults to `CLR_STRING`, so
neither client can currently declare a UUID column at all.

## Known issues / accepted as out of scope

**No migration path for existing text-keyed tables.** A deployment carrying one must alter the
column by hand before the type will re-register. The schema-drift detector will report the
mismatch, but this spec adds no automated migration.

**`Iverson.LoadTest`'s hardcoded `@id::uuid`** (`WritePathRunner.cs:203`) is left alone. It queries
`BenchmarkArticle`, whose key is a Guid, so it is correct today — but it repeats the assumption
this spec makes explicit elsewhere.

**The `::uuid` casts in `EntityRepository` remain hardcoded.** They become correct by construction
rather than by defensive coding. If the UUID-key invariant is ever relaxed, they are the code to
revisit.
