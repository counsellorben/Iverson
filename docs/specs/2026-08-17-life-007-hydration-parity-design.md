# IVC-LIFE-007 hydration parity

## Problem

`IVC-LIFE-007` — "the entity returned by a depth-resolved read is hydrated at that depth" — passes
for .NET and fails live for Java, Python, TypeScript and Go. The standard's
`#### Known non-conformance` section attributes all four failures to one cause: "their typed model
classes declare no field to receive a hydrated relation object."

That is true for three clients and **false for Java**. `JavaArticle` declares `javaAuthor`,
`javaTags` and `javaTag` with getters and setters, exactly like the passing .NET model. Java fails
because its client library discards the data on arrival: `StructConverter.fromStruct` contains
`if (isNavigationProperty(f) ) continue;`, excluding navigation fields from the read-side field map.
The identical check in `toStruct` is correct — the write contract is FK-only — and appears to have
been applied to the read path by symmetry.

So there are two defects behind one requirement ID:

1. **Java** — a read path that deliberately drops hydrated children it was handed.
2. **Python, TypeScript, Go** — the relation is declared *on* the foreign-key member
   (`py_author_id = many_to_one("PyAuthor")`, `GoAuthorId string` with tag `many_to_one:GoAuthor`,
   `@ManyToOne(() => TsAuthor) tsAuthorId`), so the derived navigation name has no member to land in.

The server is not implicated. It hydrates correctly for every relation kind and already recurses
for depth > 1.

## What this document is

The design for making hydration work in all five clients regardless of how a relation is declared,
and for rewording `IVC-LIFE-007` so it stops encoding one language's object shape. It changes no
model's declaration style and does not touch the write path.

## Decisions taken

1. **Hydration must work regardless of declaration style.** The alternative — "declare a navigation
   member if you want hydration" — would have made the three FK-member-style clients conform by
   editing their *conformance driver models* rather than their libraries, turning the harness green
   without changing any client capability. That is gaming the test unless the declaration style is
   genuinely a documented user choice, and it is not.

2. **The requirement is judged on what the client hands the caller, in whatever shape the language
   allows.** Go structs have a fixed field set at compile time; `reflect` can set fields, never add
   them, so "materialize a navigation member the model never declared" is not expressible in Go at
   all. Judging on reachability rather than on a member of a particular name or shape is the only
   version of decision 1 that is true across five languages. The cost is that Go's proof is a map
   entry rather than a typed field.

3. **Hydrated children are typed instances, not untyped bags.** A Python caller reaching
   `article.py_author.name` rather than `article.py_author["Name"]` is the difference between an SDK
   and a deserialization accident. The cost is a type-name→type registry in Python and Go, and a new
   accessor in TypeScript.

4. **The member the caller touches is language-idiomatic**, derived from the foreign-key member by
   the same suffix strip each client already performs to produce the registered navigation name —
   stopping one step earlier, before the PascalCase conversion. `py_author_id` → `py_author`,
   `tsAuthorId` → `tsAuthor`. Go keeps a map key, since a map key is not an identifier and gains
   nothing from idiom. The consequence is that the name the standard discusses and the name the
   caller types are different strings in two clients, which is why decision 2's reachability framing
   is load-bearing rather than cosmetic.

5. **The write path is untouched.** Every client keeps excluding navigation members from what it
   sends. Two clients need *new* exclusions to preserve that (see "Write-path regressions").

## The wire contract (already settled, verified)

The server writes each hydrated child under `relation.PropertyName` — the exact navigation name that
client registered — for every relation kind:

| Kind | Resolver | Shape |
| --- | --- | --- |
| `many_to_one`, `one_to_one` | `ResolveSingleRelationAsync` | `Value.ForStruct` |
| `many_to_many` | `ResolveManyToManyAsync` | `Value.ForList` of structs |
| `one_to_many` | `ResolveOneToManyAsync` | `Value.ForList` of structs |

Each resolver recurses itself when `depth > 1`, so a client never has to request nesting — it only
has to deserialize what arrives. All five clients derive the same PascalCase navigation name from
the same suffix-strip rule, so every client already knows the key to look for. No new wire
convention is introduced.

## Per-client change

### .NET — none

`StructConverter.FromStruct` is a JSON round-trip through `System.Text.Json`. Nested structs bind to
navigation properties by name with no relation logic at all. This is why .NET passes, and it is the
reference behaviour.

### Java — two changes

- Remove `if (isNavigationProperty(f)) continue;` from `fromStruct`'s field-map construction. The
  identical check in `toStruct` stays; it is the write contract.
- Add a `STRUCT_VALUE` case to `fromValue(Value, Class<?>, Type)`, recursing into
  `fromStruct(value.getStructValue(), targetType)`.

The second is not optional. `fromValue` currently has no `STRUCT_VALUE` case, so a nested struct
falls through to `default -> null`; the list branch recurses into the same function, so lists of
structs null out too. Removing the skip alone would set every navigation field to `null` — a change
that looks like a fix, compiles, and hydrates nothing.

Java's navigation field names already PascalCase to the registered `PropertyName` (`javaAuthor` →
`JavaAuthor`), so no name derivation is added.

### Python — one change

`_from_struct` gains a second pass over the relation set. For each relation, look for the PascalCase
wire key; if present, resolve the related class from the registry, recurse, and `setattr` under the
idiomatic member name (the foreign-key member minus its `_id` / `_ids` suffix).

No write-path exclusion is needed, and this is a verified property rather than an assumption:
`_entity_to_struct` iterates `__annotations__`, and a dynamically-set attribute is not in
`__annotations__`.

### TypeScript — three changes

- A new accessor that preserves the related-type constructor. `getRelations` collapses it to
  `relatedType: typeFactory().name` — a string — so the constructor survives only in the raw
  `IVERSON_RELATIONS` metadata.
- `payloadToEntity` hydrates into `instance[navMember]`, with `navMember` the field minus its
  `Id` / `Ids` suffix.
- `entityToPayload` excludes hydrated navigation members (see "Write-path regressions").

### Go — three changes

- Exclude the hydration carrier from extraction/registration. `ExtractMeta` iterates every field
  including untagged ones, so a declared `Hydrated map[string]any` would reach `goTypeToClr`, which
  has no mapping for a map type — `Register` would fail outright. The field cannot simply be
  declared.
- `structToEntity` populates `Hydrated`, keyed by the wire name, values typed pointers boxed in
  `any`. The existing `OneToMany` skip is removed for exactly the reason it exists: the comment there
  states the server injects hydrated child structs under the field's own name, and the code then
  discards them.
- Exclude `Hydrated` from `entityToStruct` (see "Write-path regressions").

### Entity-type registry (new: Python, Go)

Python stores `related_type` as a bare string; Go stores `RelatedType string`. Neither has anything
mapping a type name back to a class or `reflect.Type`.

- **Python** registers each class into a name→class map at decoration time, when the entity decorator
  runs at import.
- **Go** registers name→`reflect.Type` inside `registrar.Register`, which every entity already passes
  through.

Both are a map and a lookup; neither is new public API. A relation naming a type that was never
registered falls back to the untyped child rather than raising — a hydration miss must not turn a
successful read into an exception.

## Write-path regressions this must not introduce

Two clients build their write payload from the *live instance* rather than from a declaration, so
hydrating an object silently changes what a subsequent write sends:

- **TypeScript** — `entityToPayload` iterates `Object.getOwnPropertyNames(entity)`. A hydrated
  `tsAuthor` would be sent back on the next write as `TsAuthor`. `getMapped` → `updateMapped` would
  violate the FK-only write contract.
- **Go** — `entityToStruct` iterates `t.NumField()`, and `Hydrated` is untagged, so it would be
  serialized under its own name.

Both need explicit exclusions. Python is provably safe; .NET and Java exclude navigation members
already.

## `one_to_many` is finished, not started

For `one_to_many` the declared member *is* the navigation member and the wire key matches it exactly.
Python and TypeScript therefore already land those children — as raw dicts and plain objects — and Go
drops them at its `OneToMany` skip. Under this design all three hydrate them into typed instances.
This is a consequence of the change, not additional scope: the same code path handles them.

## The requirement

`IVC-LIFE-007` is retired and re-authored. Its current statement — that the returned entity "is
hydrated at that depth", graded by finding a navigation property carrying an object with its own key
— encodes .NET's object shape, which is what made four clients that reach and materialize the data
fail it. The successor asserts that a depth-resolved read makes the related object reachable through
the client's own object model, carrying its own key.

Per the standard's statement-cell immutability convention this is a retirement plus a new ID, not an
edit — the path `IVC-REG-001` and `IVC-LIFE-005` both took.

Two document changes follow in the same commit, or the gate breaks:

- LIFE's `#### Coverage` ledger cites `IVC-LIFE-007` under "Depth-resolved read hydration". Retiring
  it leaves a `Retired` ID in an Evidence cell, firing the axis-completeness check's failure mode 3.
- `#### Known non-conformance` states the premise this document opens by falsifying. It is corrected
  or removed according to what still fails after the change.

`Requirements.cs`'s `LifeDepthResolvedReadHydrated` const is renumbered to the successor ID; leaving
a const on a retired ID fails check 1.

## What this does not do

- It does not change any model's declaration style. No client is asked to move a relation off its
  foreign-key member.
- It does not touch the write path's contract, only guards it against two new leaks.
- It does not give Go a typed navigation field. Go's caller performs a type assertion on
  `Hydrated["GoAuthor"]`. That is inherent to `map[string]any` and was accepted when decision 2 was
  taken.

## Verified assumptions

| # | Assumption | Result |
| --- | --- | --- |
| A1 | Java's `fromStruct` navigation skip is what blocks hydration | Holds — `StructConverter.java:68` |
| A2 | `isNavigationProperty` identifies navigation fields by relation annotation | Holds |
| A3 | `fromStruct` can recurse into a navigation field's declared type as written | **FALSE.** `fromValue(Value, Class<?>, Type)` (`:172-200`) has no `STRUCT_VALUE` case; nested structs hit `default -> null`, and the list branch recurses into the same function. Java needs a second change |
| A4 | The Java navigation field name maps to the registered `PropertyName` | Holds — `javaAuthor` → `JavaAuthor` via `toPascalCase` |
| A5 | A Python entity decorator runs at import carrying `type_name` | Holds — `annotations.py:221`, `_iverson_meta["type_name"] = cls.__name__` |
| A6 | Python relation metadata carries `field`, `kind`, `related_type` | Holds — `annotations.py:198-215` |
| A7 | Python has no name→class registry | Holds — no such map anywhere in `iverson_client` |
| A8 | The derived Python navigation member cannot collide with a declared annotated field | **Partially holds.** It cannot collide with its own foreign-key member — the suffix-stripped form is a distinct string. It is *not* guarded against a model that separately declares a field of that name; the server's collision check guards registered `PropertyName`/`ForeignKey`, not a client-local member name. The implementation must reject or detect that collision itself |
| A9 | TypeScript's `getRelations` exposes the related type as a callable factory | **FALSE.** It collapses to `relatedType: typeFactory().name`, a string (`annotations.ts:387-394`). The factory survives only in the raw `IVERSON_RELATIONS` metadata; a new accessor is required |
| A10 | `payloadToEntity`'s instance accepts undeclared properties | Holds — `Object.create(cls.prototype)`, `core.ts:487` |
| A11 | The derived TypeScript navigation member cannot collide | **Partially holds** — same as A8, and the same obligation on the implementation |
| A12 | `registrar.Register` sees every entity type | Holds — `registrar.go:65`, `reflect.TypeOf(e)` |
| A13 | A `Hydrated map[string]any` field survives registration | **FALSE.** `ExtractMeta` iterates every field including untagged ones (`tags.go:228-231`); `goTypeToClr` has no map mapping, so `Register` fails. An extraction/registration exclusion is required |
| A14 | `structToEntity` is the sole read path for `GetMapped` | Holds — `coordinator.go:614`, reached from `:236` |
| A15 | Hydrated children arrive under `relation.PropertyName` for every kind | Holds — `EntityRelationResolver.cs:96` (single), `:137` (many-to-many), `:176` (one-to-many); each recurses itself for depth > 1 |
| A16 | The hydrated child carries its own key field | Holds — each resolver parses a full stored row; masking removes disallowed fields only |
| A17 | .NET hydrates today and needs no change | Holds — `StructConverter.cs:47-51` is a `System.Text.Json` round-trip, so nested structs bind by name |
| A18 | `Verifier.VerifyDepthCapability` is the assertion that must change | Holds — `Verifier.cs:443-448`, judged from the driver's own depth-1 entity |
| A19 | Retiring the ID forces a LIFE `#### Coverage` ledger update | Holds — the ledger's "Depth-resolved read hydration" row cites `IVC-LIFE-007`; a `Retired` ID in an Evidence cell fires the axis-completeness check's mode 3 |
| A20 | Retiring it requires removing/renumbering its const | Holds — `Requirements.cs:164`, `LifeDepthResolvedReadHydrated` |
| A21 | Go's write path would serialize an undeclared-tag `Hydrated` field | Holds — `entityToStruct` iterates `t.NumField()`; untagged fields are serialized under their own name |
| A22 | A dynamic Python attribute cannot leak into the write path | Holds — `_entity_to_struct` iterates `__annotations__` (`core.py:396-406`); a dynamically-set attribute is not there |
| A23 | A dynamic TypeScript property would leak into the write path | **Holds, and is a defect to prevent.** `entityToPayload` iterates `Object.getOwnPropertyNames(entity)` on the live instance (`core.ts:470`), so `getMapped` → `updateMapped` would send the hydrated child |

## Known issues

- Go's conformance proof is weaker than the other four clients': a map entry carrying a key rather
  than a typed field. Accepted under decision 2 — the language admits nothing stronger without
  requiring a declared navigation field, which decision 1 rejected.
- The registry's unregistered-type fallback means a hydration miss is silent at the library level.
  It is not silent at the harness level, where `IVC-LIFE-007`'s successor asserts reachability, but a
  library caller sees an untyped child rather than an error.
