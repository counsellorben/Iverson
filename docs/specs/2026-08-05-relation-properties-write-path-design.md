# Relation Properties on the Write Path — Design

**Date:** 2026-08-05
**Status:** approved, not implemented
**Base:** `origin/main@b4b2d27`

## Problem

Two defects prevent a field-restricted caller from writing an entity that declares a relation, and
silently corrupt the write when it does get through.

### 1. Relation properties are rejected by field authorization

`AuthorizationDecision.AllowedFields` is composed from the key column, scalar columns, FK columns,
and vector/chunk property names (`RowFieldAuthorizationEvaluator.cs:69-74`). Relation property
names are absent. `RejectDisallowedFields` therefore refuses any payload containing one:

```
Field(s) not permitted for this caller: Body, Author
```

This is not cosmetic. `RelationValidator` treats a relation property as a *supported alternative*
to the FK column — its own error text advertises the pair:

> `Relation 'Author' (ManyToOne) is required. Provide 'AuthorId' (GUID reference) or 'Author'
> (embedded object).`

But `EnforceWriteAuthorization` runs *before* `ValidateRelations` at every write entry point
(`ObjectPersistenceGrpcService.cs:33` then `:43`; `:104` then `:114`;
`ObjectMappingGrpcService.cs:294` then `:298`; `:341` then `:351`). Authorization rejects the
property before the validator that supports it ever runs, so **embedded-object relation writes are
impossible today for any caller subject to a `FieldPermission`.**

The read path is unaffected: `EntityRelationResolver` injects relation properties *after*
`MaskDisallowedFields` runs (`ObjectMappingGrpcService.cs:273` then `:276`), so they were never
masked. This is a write-side defect only.

### 2. Embedded-object references never populate the FK column

When a caller supplies the relation property as an existing-entity reference (a nested object
carrying only the related key) and omits the FK column, `ValidateSingleRelation` accepts it and
returns. Nothing then copies the nested key into the FK column: the payload goes straight to
`StructSerializer.SerializePayload` (`ObjectPersistenceGrpcService.cs:51`) and `json_populate_record`
ignores the unmatched nested key. The row is written with a NULL FK.

No write path consumes nested relation structs — `EntityRelationResolver` is read-only and
`RelationValidator` only validates — so the embedded form is advertised as equivalent to the GUID
form and is not.

### 3. The .NET client cannot omit a null FK (discovered during verification)

`StructConverter.ToStruct` serializes with a camelCase policy and no null-ignoring condition, so a
POCO with a null nullable FK and a null navigation property produces:

```
KEY[id]       kind=StringValue
KEY[authorId] kind=NullValue
KEY[author]   kind=NullValue
```

`StructFieldAccess.GetFieldValue` tries the canonical name then its camelCase form, so
`GetFieldValue(payload, "AuthorId")` **finds** `authorId` and returns a `NullValue`. The FK branch
is taken, `fkValue.StringValue` is `""`, and validation fails with *"must be a valid non-empty
GUID."*

Two consequences: a nullable FK left null fails on write even though the validator explicitly
intends it to be omittable (`fkCol.IsNullable` → no error), and the embedded-object branch is
**unreachable from the .NET client**, because the FK key is always present.

## Goal

A field-restricted caller can write an entity carrying relation properties; an embedded
existing-entity reference actually populates the FK column; and a keyless embedded object fails
loudly instead of writing a NULL FK.

## Design

### 1. Relation property names join the write-side allowed-field set

In `RowFieldAuthorizationEvaluator`, extend the `allFields` composition:

```csharp
.Concat(action == AuthorizationAction.Write
    ? schema.Relations
        .Where(r => r.Kind == RelationKind.OneToMany || !excluded.Contains(r.ForeignKey))
        .Select(r => r.PropertyName)
    : [])
```

**Rule (C′): a relation property is writable exactly when its FK column is writable.** Writing
`Author` is writing `AuthorId`; one permission governs one concept. Without this, an admin who
restricts `AuthorId` gets no protection from a caller who sends `Author` instead — a hole that is
harmless only while embedded objects never reach storage, which section 2 changes.

`OneToMany` is the carve-out: its FK lives on the *related* entity, so there is no local column to
gate on. It is permitted unconditionally — inert on write (the validator skips this kind) and
injected on read after masking. This is the same kind-gate subtlety that
`AllowedFields.Contains(r.ForeignKey)` got wrong in the GetSchema work, where it silently dropped
every `OneToMany` relation under any `FieldPermission`.

**Write actions only.** `AllowedFields` is also consumed by `ObjectSearchGrpcService` for filter,
sort, and vector/chunk authorization (`:101`, `:140`, `:150`, `:312`). Adding relation names for
read actions would widen search permissions, which nothing requires — reads never need them.

The existing trailing `.Where(f => !excluded.Contains(f))` still applies, so a `FieldPermission`
naming the property directly (`"Author"`) continues to restrict it. Both spellings stay governable.

Payload keys pass through `StructSerializer.UpperFirst` before comparison
(`AuthorizationFieldMasking.cs:155`), so the client's camelCase `author` matches the schema's
`Author` without further work.

The `AllowedFields` doc comment on `AuthorizationDecision` enumerates the set's contents and omits
relations; it is updated. That stale comment is part of why this went unnoticed.

### 2. Validation also normalizes embedded references into the FK column

`IRelationValidator.ValidateRelations` becomes `ValidateAndNormalizeRelations` — the name states
that it mutates the payload. All four write paths already call it, so the rename adds no new call
site anyone can forget; a separate normalization step would need wiring into four places and would
silently miss a fifth.

Per relation kind:

- **`ManyToOne` / `OneToOne`** — FK column present and non-null → unchanged (validate GUID). FK
  absent *or null* and the nav property is a nested object → if it carries a valid key, copy that
  key into the FK column; if it is keyless, reject.
- **`ManyToMany`** — the same over the list: each item's key is copied into the FK list column in
  order; any keyless item rejects. FK names derive from properties ending `Id`/`Ids`
  (`SchemaBuilder.cs:106-111`), so the plural FK (`TagIds`) is a real declared column. An *empty*
  FK list is a supplied value, not an absent one — it means "no related entities" and must not
  trigger normalization from the nav property. Only a missing or `NullValue` FK is absent.
- **`OneToMany`** — skipped, as today. The FK lives on the related entity; there is nothing to copy.

**A `NullValue` FK counts as absent.** Without this the nav-property branch is unreachable from the
.NET client (section 3 of Problem) and nullable FKs fail on write. `Value.KindCase == NullValue` is
the test; a JSON `null` navigation property is likewise not an embedded object and falls through to
the existing required/nullable check, which the current `navValue?.StructValue is { } nested`
pattern already handles.

**Writing the FK must strip case-variant keys first.** The client sends `authorId`; setting
`AuthorId` alongside it leaves both in the Struct, and `SerializePayload` upper-firsts every key
into a `Dictionary`, throwing *"An item with the same key has already been added. Key: AuthorId."*
This is the identical failure fixed for `TenantId`/`OwnerId` in `361dc0c`; normalization reuses
that `SetAuthoritativeField` approach rather than assigning directly.

**Keyless embedded objects are rejected**, with a message naming the property and pointing at the
by-key form. Supporting them means cascade-inserting the related entity — key generation, ordering,
transactional rollback, and a separate authorization evaluation against the related type — which is
its own design, not this one. Today's behavior for that input is a silent NULL FK; this makes it an
explicit error.

### 3. Ordering makes the two parts inseparable

Authorization runs before validation, so normalization writes the FK column *after* the field
check. Part 1 is what makes Part 2 safe: a caller barred from writing `AuthorId` is also barred
from sending `Author`, so the request is rejected before normalization can run. **Part 2 must not
ship without Part 1.**

### 4. Testing

`RowFieldAuthorizationEvaluatorTests`:

1. `ManyToOne` nav property allowed on write when its FK is allowed
2. `ManyToOne` nav property excluded on write when its FK is excluded
3. `OneToMany` nav property allowed on write despite having no local FK column
4. A `FieldPermission` naming the property directly still excludes it
5. Relation names absent from `AllowedFields` for read actions

`RelationValidatorTests`:

6. `ManyToOne` existing-entity reference → FK column populated with the nested key
7. `ManyToOne` keyless embedded object → throws, message names the property
8. `ManyToOne` FK already present → nav property ignored, FK untouched
9. `ManyToOne` FK present as `NullValue` + valid embedded reference → FK populated, and
   `SerializePayload` does not throw a duplicate-key error
10. `ManyToOne` FK present as `NullValue`, nullable column, no nav property → no error
11. `ManyToMany` list of references → FK list populated in order
12. `ManyToMany` list containing a keyless item → throws
13. `OneToMany` nav property → no FK written, no error
14. Existing `ValidateRelations_NestedExistingEntityWithExtraProperties_Throws` still passes

`AuthorizationFieldMasking`:

15. A write payload carrying a nested relation struct is not rejected for a field-restricted caller

## Out of scope

- **Cascade-inserting new related entities** from keyless embedded objects. Now an explicit error
  rather than a silent NULL FK. Deserves its own spec: it needs cross-type authorization,
  transactional rollback, and store targeting.
- **Client-side changes.** All five clients keep serializing relation properties; the server owns
  the rule, so a hand-rolled gRPC caller gets the same behavior.
- **Making `StructConverter` omit nulls.** Fixing the client to drop null keys would also address
  the section-3 symptom for .NET, but leaves the other four clients and hand-built payloads
  exposed. The server-side `NullValue`-as-absent rule covers all of them.

## Known issues — pre-existing, not addressed here

- **No registration-time guard against a relation `PropertyName` colliding with a scalar or FK
  column name.** Searched `SchemaValidator`/`SchemaRegistrationOrchestrator` and found none. The
  client attribute models make it structurally unlikely — a property is either scalar or relation —
  but nothing enforces it. If such a schema were registered, the C′ gate would produce ambiguous
  results. Accepted as out of scope (Ben, 2026-08-05).
- **`excluded` uses an ordinal, case-sensitive `HashSet`** while the key-column comparison beside it
  uses `OrdinalIgnoreCase`. The new FK check follows the surrounding ordinal style rather than
  changing it.

## Verified assumptions

Verified against `origin/main@b4b2d27`. 18 listed cold before any verification; 16 held, 2 failed
and changed the design.

| # | Assumption | Result |
|---|---|---|
| A1 | `AllowedFields` has a single construction site | HOLDS — `RowFieldAuthorizationEvaluator.cs:55,75` |
| A2 | `schema.Relations` reachable in the evaluator | HOLDS — `Evaluate(SchemaDescriptor, …)` |
| A3 | `RelationKind` has exactly 4 values | HOLDS — `SchemaDescriptor.cs:67` |
| A4 | `RelationDescriptor.ForeignKey` matches the FK column name | HOLDS — `SchemaBuilder.cs:106-111`, both derive from `Id`/`Ids` properties |
| A5 | `RejectDisallowedFields` upper-firsts payload keys | HOLDS — `AuthorizationFieldMasking.cs:155` |
| A6 | Read masking precedes relation injection at every read site | HOLDS — `ObjectMappingGrpcService.cs:273` then `:276`; Retrieval/Search resolve no relations |
| A7 | No other `AllowedFields` consumer breaks when relation names are added | **FAILED** — `ObjectSearchGrpcService.cs:101,140,150,312` uses it for filter/sort/vector auth. Design changed: write actions only |
| A8 | No `PropertyName`/column-name collision is possible | **UNVERIFIED** — no guard found; recorded as a known issue |
| A9 | `ValidateRelations` has exactly 4 call sites, all writes | HOLDS — Persistence `:43,:114`; Mapping `:298,:351` |
| A10 | One implementation, DI-registered | HOLDS — `Program.cs:191`; one test substitute |
| A11 | Payload mutations reach `SerializePayload` | HOLDS — same `request.Payload` reference, `:43` → `:51` |
| A12 | The `ManyToMany` FK is a real declared column | HOLDS — `SchemaBuilder.cs:106-111` accepts `Ids` properties |
| A13 | `StructFieldAccess` casing behavior | HOLDS — canonical then camelCase (`StructFieldAccess.cs`) |
| A14 | Setting the FK cannot create a duplicate key | **FAILED** — probe showed the client emits `authorId`/`author` as camelCase `NullValue`. Design changed: `NullValue` FK counts as absent, and normalization strips case-variants |
| A15 | Authorization precedes validation at all 4 sites | HOLDS — `:33`<`:43`, `:104`<`:114`, `:294`<`:298`, `:341`<`:351` |
| A16 | `isExistingEntity` is the right reference/new discriminator | HOLDS — non-empty, parseable, non-`Guid.Empty` |
| A17 | Existing test helpers can express the new cases | HOLDS — `SchemaWithAuthorization` (`Relations = []`), `MakeSchemaWithRelation` |
| A18 | Nothing depends on the current behavior | HOLDS — one existing nav-property test, none asserting the FK stays unset |

A14 was verified by a temporary probe test that serialized a POCO with a null nullable FK and a
null navigation property through `StructConverter.ToStruct` and dumped the resulting Struct keys.
The probe was deleted after reading its output.
