# Vector-search entity binding: canonical Struct keys and typed values

**Date:** 2026-08-24
**Status:** design approved, not implemented

## The problem

`vector-search` fails for python, typescript and go in the live conformance matrix, and passes for
dotnet and java. It is the only failing scenario; the matrix is otherwise 38 ok / 9 skip / 3 FAIL.

The three failing clients report no usable rows. The failure is silent: an entity whose fields are
all unbound is indistinguishable from "the search returned nothing" unless something compares a
payload field, which is why five green client unit suites and every prior matrix run missed the
cause and recorded it as a flake family.

## Root cause

`ObjectSearchGrpcService.SearchSimilar` builds its response `Struct` from the Qdrant payload,
copying keys verbatim:

```csharp
var protoStruct = new Struct();
foreach (var kvp in r.Payload)
    protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value);
```

Qdrant payload keys are camelCase — the write path stores them as `col.Name.ToCamelCase()`
(`IntelligenceStoreConsumer.cs:435`). The mapped read path emits the descriptor's own property
names. So the same entity type is returned under two different key conventions depending on which
RPC produced it.

Python's `_entity_from_struct` PascalCases each model field and looks that key up, so on the vector
path nothing matches and every field stays `None`. Go yields its zero value (`""` — hence its "1
distinct label"), TypeScript `undefined`. dotnet and java survive only because they bind
case-insensitively: `PropertyNameCaseInsensitive = true` in `StructConverter.cs:15`, and lowercasing
both sides in `StructConverter.java:68`. Neither is evidence that the wire is correct.

Evidence the server is otherwise healthy, gathered against the live stack: the request bytes are
identical across all five languages; the RPC returns `hits: 1` with a correct score; the wire payload
is fully populated (`label: "vec-python"`, `title`, `marker`, `key`, `body`); the projection wait was
satisfied; and Qdrant holds the point with both named vectors. Only the key names are wrong.

A second defect sits on the same line. `Value.ForString(kvp.Value)` emits every value as a string, so
a client binding an `int` or `bool` property from a vector result receives a string. Qdrant's own
payload values are typed (`IntegerValue`, `DoubleValue`, `BoolValue`); `Iverson.Vector` flattens them
to string at its DTO boundary (`IntelligenceVectorService.cs:107`).

A third: the payload's entity key is `key`, while the mapped read path emits the key column's name.
Casing alone would leave every client's identifier property unbound on the vector path — and the
conformance assertion would not notice, because it compares labels.

## Design

One site changes: the response loop in `ObjectSearchGrpcService.SearchSimilar`
(`Iverson.Api/Grpc/ObjectSearchGrpcService.cs:278-282`).

**1. Canonical key names.** Build a lookup from camelCased descriptor name to descriptor name, over
the schema's scalar columns and its key column, and resolve each payload key through it. The payload
key `key` maps to the key column's own name. A key not in the lookup falls back to
`StructSerializer.UpperFirst`. That fallback is load-bearing, not a diagnostic edge case: vector
fields (`:422`) and FK columns (`:440`) are written as `ToCamelCase(name)` but live outside
`ScalarColumns`, so they always take it — and it is correct for them because `UpperFirst` is the
exact inverse of `ToCamelCase`.

**2. Typed values.** Convert the payload string to a proto `Value` whose kind follows the descriptor
column's `SqlType`. The vocabulary is bounded and known, so the mapping is stated exhaustively rather
than left to a predicate: `INTEGER`, `INT`, `BIGINT`, `REAL`, `FLOAT`, `DOUBLE`, `DOUBLE PRECISION`
to `Value.ForNumber`; `BOOLEAN` to `Value.ForBool`; `TEXT`, `STRING`, `UUID`, `TIMESTAMPTZ`,
`DATETIME`, `BYTEA`, `VARBINARY` to `Value.ForString`. Array types keep the string form — the payload
flattening upstream gives no list back to reconstruct, and inventing one would be a guess. Timestamps stay strings, matching both `ToProtoValue`'s `DateTime` case and the
ISO-canonical form the write path already stores (`IntelligenceStoreConsumer.cs:772`). A value that
does not parse falls back to the string form rather than failing the row. A payload key with no
descriptor column emits `Value.ForString` — correct for all three producers outside `ScalarColumns`,
since FK columns are written as `TEXT` (`:439`), vector fields as extracted text (`:422`), and `key`
is a string (`:417`). Stating this matters: without it, "convert using the descriptor column's
`SqlType`" reads as licence to skip keys that have none, which would silently drop every foreign key
from vector results.

**3. Masking.** `MaskDisallowedFields`'s `exemptField: "Key"` becomes the key column's name. Without
this the masking strips the identifier as soon as the keys become canonical.

The result is that the Qdrant path emits what the StarRocks-backed paths already emit via
`DictToProtoStruct` + `ToProtoValue`: descriptor property names, typed values.

### Why the server and not the clients

Three clients bind case-sensitively and two do not. Fixing the clients means three changes in three
languages and three releases, and leaves the wire contract inconsistent, so the next surface that
returns camelCase has the same trap waiting. Fixing the server is one site and corrects all five
clients at once. PascalCase is canonical by incumbency on the mapped read path and by what
python/typescript/go already expect; dotnet and java are casing-agnostic and impose no constraint.

### Testing

- A unit test asserting the emitted `Struct`'s keys equal the descriptor's property names and that a
  numeric column arrives as `Value.ForNumber`, not a string. Host: `Iverson.Api.Tests/Grpc/`, which
  has both `ObjectSearchGrpcServiceTests.cs` and `ObjectSearchVectorIntegrationTests.cs`.
- The live conformance matrix as the end-to-end check: `vector-search` green for all five languages.

## Out of scope

- The 9 matrix skips. Eight are the by-design "this scenario has no client-library leg" case and one
  is java's inability to express a misnamed foreign key. Each is its own piece of work, sequenced
  after this one.
- `Iverson.Vector`'s flattening of typed payload values to string. Fixing it at source would remove
  the need to re-parse, but `VectorSearchResult.Payload` has 47 consumers, several on the chunk path.
  Ben chose re-parsing at the Api layer to keep this change contained.

## Known issues / accepted as out of scope

**The type loss is repaired downstream, not at its source.** Qdrant returns typed values and
`Iverson.Vector` discards the types one layer before the Api sees them; this design re-parses from
the descriptor rather than preserving what was never meant to be lost. Accepted by Ben on
2026-08-24 to keep the change to one file. A future change to `VectorSearchResult.Payload` should
delete the re-parsing rather than layer on it.

**Case-insensitive clients stay case-insensitive.** dotnet and java will continue to bind either
casing, so neither can detect a future regression of this defect. The unit test is the guard.

## Verified assumptions

| Assumption | Evidence |
|---|---|
| Only one site turns a Qdrant payload into a response Struct | `ObjectSearchGrpcService.cs:278`; the only other `new Struct()` is `DictToProtoStruct` at `:926`, fed by SQL result rows |
| StarRocks-backed paths already emit descriptor names and typed values | `Search`/`GroupBy`/`Pipeline` route through `DictToProtoStruct` + `ToProtoValue` (`:926-944`) |
| The Qdrant payload has exactly four key producers | `IntelligenceStoreConsumer.cs:417` literal `key`; `:422` vector fields; `:435` scalar columns incl. `__TenantId`; `:440` FK columns — all but `:417` keyed as `ToCamelCase(name)` |
| The payload's key field is literally `key` | Wire dump from a live python run: `fields { key: "key" ... }` |
| The mapped read path emits descriptor property names | python's `crud-roundtrip` passes while `vector-search` fails, and its binder matches PascalCase only |
| `schema` is in scope at the change site | used at `ObjectSearchGrpcService.cs:136` in the same method |
| PascalCase does not break the passing clients | `StructConverter.cs:15` `PropertyNameCaseInsensitive = true`; `StructConverter.java:68` lowercases both sides |
| Masking still works, but `exemptField` must change | `AuthorizationFieldMasking.cs:208` compares `UpperFirst(key)`; `"Key"` would no longer be the identifier's name |
| Payload values reach C# as strings | `IVectorRoles.cs:52` `IReadOnlyDictionary<string, string>`; flattened at `IntelligenceVectorService.cs:107` |
| `.Payload` has many consumers, so widening the DTO is not contained | 47 usages across `Iverson.Api` and `Iverson.Vector` |
| The SqlType vocabulary is bounded and known | 15 values in `SchemaBuilder.ScalarTypeMap` / `ArrayTypeOverrides` |
| No existing string-to-typed-value parser to reuse | `IntelligenceFilterBuilder.Canonicalize` canonicalises timestamps on the filter side only |
| A test project exists to host the guard | `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, `ObjectSearchVectorIntegrationTests.cs` |
| The harness does not depend on the response `key` | `VectorSearchScenario` compares labels and `ChunkSearchResponse.ParentKey` |

Two assumptions were **falsified** during verification and changed the design:

- *"The server does not normalise the filter property, so the filter never matches."* False — the
  server does camelCase filter properties (`ObjectSearchGrpcService.cs:162`). The filter was never
  the problem; this hypothesis would have produced a fix for a defect that does not exist.
- *"Type restoration can reuse `ToProtoValue`."* False — `ToProtoValue` takes `object?`, and only
  strings survive to that point. This is what forced the re-parse-versus-widen-the-DTO decision.
