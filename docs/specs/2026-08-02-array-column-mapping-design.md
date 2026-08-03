# Array Column Mapping — Design

**Date:** 2026-08-02
**Status:** Approved, awaiting critical design review
**Scope:** complete the server's array type mappings, add schema-drift detection, and give the four clients that lack it the ability to declare an array. No proto change.

## Problem

Arrays are declarable in one client and storable for two element types. The two sets barely overlap.

**The .NET client accepts arrays of all nine scalar types**, plus `List<T>` — `SchemaRegistrar.cs:243-264`
unwraps `T[]` and `List<T>` and returns `isArray: true` for `string`, `Guid`, `int`, `long`, `float`,
`double`, `bool`, `DateTime` and `DateTimeOffset`.

**The other four clients never emit `is_array` at all.** Python hardcodes `is_array=False`
(`core.py:173`), TypeScript hardcodes `isArray: false` (`core.ts:249`), and Java and Go never set
the field, taking the proto default. So an array property in those four registers as a scalar by
client design.

**The server has array SQL mappings for two of the nine.** `ArrayTypeOverrides`
(`SchemaBuilder.cs:250-255`) holds exactly `ClrGuid → "UUID[]"` and `ClrFloat → "REAL[]"`.
`ClrTypeToSql` (`:269-275`) falls through to the *scalar* map for everything else, so a .NET
`string[]` becomes `TEXT`, an `int[]` becomes `INTEGER`, and so on for seven of nine.

That column type reaches DDL verbatim (`PostgresSchemaManager.cs:37`), and the consequence is a
broken read rather than a degraded one. Writes go through
`json_populate_record(null::"{TableName}", @Json::json)` (`OutboxWriter.cs:27`); reads through
`row_to_json(t)::text` (`EntityRepository.cs:9`), then `JsonParser.Default.Parse<Struct>`
(`ObjectMappingGrpcService.cs:81`), then `JsonSerializer.Deserialize<T>` on the client
(`StructConverter.cs:27-32`). With a `TEXT` column, `row_to_json` emits the stored text **as a JSON
string**, so the client receives `"[\"a\",\"b\"]"` where its `string[]` property expects an array
and deserialization fails.

No test fixture, sample or load-test entity anywhere in the repo declares an array property, which
is why this has stayed invisible.

## Goal

Arrays that work end to end: declarable in all five clients, stored in a real array column, and
round-tripping intact — with any pre-existing column that disagrees reported rather than ignored.

## Design

### 1. Complete the type mappings

`ArrayTypeOverrides` becomes total over `ClrType`:

| ClrType | Postgres | StarRocks | Payload kind |
|---|---|---|---|
| `ClrGuid` | `UUID[]` *(existing)* | `STRING` | `Keyword` |
| `ClrFloat` | `REAL[]` *(existing)* | `STRING` | `Keyword` |
| `ClrString` | `TEXT[]` | `STRING` | `Keyword` |
| `ClrInt32` | `INTEGER[]` | `STRING` | `Integer` |
| `ClrInt64` | `BIGINT[]` | `STRING` | `Integer` |
| `ClrDouble` | `DOUBLE PRECISION[]` | `STRING` | `Float` |
| `ClrBool` | `BOOLEAN[]` | `STRING` | `Boolean` |
| `ClrDatetime` | `TIMESTAMPTZ[]` | `STRING` | `Datetime` |
| `ClrBytes` | `BYTEA[]` | `STRING` | `Keyword` |

**StarRocks stays `STRING` for every array.** That is what both existing array entries already do,
and `StarRocksSchemaManager` is `CREATE TABLE IF NOT EXISTS` only (`:16`) with no `ALTER` anywhere
in the project — so switching the existing two to `ARRAY<…>` would create a representation no
migration path can apply. One convention, matching what ships today.

**Payload kinds stay element-typed** rather than collapsing to `Keyword`: Qdrant indexes a list
under the same kind as its elements, so an `Integer` index over an integer list stays
range-filterable. This leaves the two existing entries' `Keyword` for `float[]` looking anomalous;
it is preserved rather than corrected, because changing it would retype a live Qdrant index.

**`ADD COLUMN` needs an array default.** `PostgresSchemaManager.cs:53-56` appends
`NOT NULL DEFAULT ('{GetDefaultForType(SqlType)}')` for every non-nullable added column, and
`GetDefaultForType` (`:146-157`) matches scalar type-name prefixes only — `INTEGER[]` matches
`StartsWith("INT")` and gets `'0'`, `TEXT[]` falls through to `''`. Every array type then produces
`ERROR: malformed array literal`. `GetDefaultForType` gains an array case returning `'{}'`, valid
for every Postgres array type, tested **before** the scalar prefix branches which would otherwise
capture it. This is the common path, not an edge case: `CREATE TABLE` emits no default and
succeeds, so a fresh type works while adding an array property to an existing type fails.
Non-nullable is the default branch — `SchemaRegistrar.cs:243-264` sets `isNullable` only from a
`Nullable<T>` unwrap, so a plain `string[] Tags` is `NOT NULL`.

**`ClrBytes → BYTEA[]` is reachable only via `byte[][]`.** `byte[]` is carved out as a scalar
before the array unwrap (`SchemaRegistrar.cs:241`). The entry exists so the table is total over the
enum rather than leaving one value falling through to the scalar map.

### 2. Schema-drift detection

`PostgresSchemaManager` currently reads only column *names* (`:24-30`, from
`information_schema.columns`) and uses them to decide what to `ADD` (`:51-58`) and what to `DROP`
(`:67-72`). It never compares types, so a column whose type no longer matches the registry is
invisible.

The query is extended to return `(name, format_type(atttypid, atttypmod))` from `pg_attribute`,
filtered `AND a.attnum > 0 AND NOT a.attisdropped`. That filter is not optional: unlike
`information_schema.columns`, `pg_attribute` also returns the six system columns (`ctid`, `xmin`, …)
and dropped-column tombstones, and the same `existingColumns` result feeds the pre-existing
orphan-`DROP` loop (`:67`) — which would then attempt `DROP COLUMN "ctid"` on every existing table.
`IF EXISTS` does not suppress it: `ERROR: cannot drop system column "ctid"`.

For every schema column whose name already exists, the actual type is compared to the expected
`ColumnDescriptor.SqlType`. New columns still `ADD`; orphans still `DROP`; only the intersection is
checked. The key column is checked too — on an existing table it was created with the table and
never revisited.

**Two callers, two consequences.** `ApplySchemaAsync` is called from
`SchemaRegistrationOrchestrator.cs:68` (registration) **and** `Program.cs:421` (startup, over every
registered descriptor). A drift policy passed by the caller distinguishes them:

- **Registration throws.** A `RegisterSchema` that would depend on a mis-typed column fails, naming
  table, column, actual type and expected type.
- **Startup logs.** An already-deployed server still boots, recording a warning with the same
  detail.

Throwing at startup would turn any historical drift — including drift unrelated to arrays — into a
boot failure. Decision made by Ben 2026-08-02.

**Type-name canonicalization.** `format_type` returns Postgres's canonical spelling, which matches
our `SqlType` strings case-insensitively for 16 of the 18 post-change strings; **both**
`TIMESTAMPTZ` and `TIMESTAMPTZ[]` differ, coming back as `timestamp with time zone` and
`timestamp with time zone[]`. Comparison goes through a `NormalizePgType` helper, guarded by an
enum-driven test (§5) so a newly added type cannot silently skip it.

**This is general, not array-specific.** A check that fired only for arrays would leave every other
type-change class drifting silently. The cost is a real behaviour change: a deployment whose table
has drifted for any historical reason now fails the registration that depends on it, where it
previously proceeded.

**StarRocks gets no equivalent.** It has no `ALTER` and its manager is `CREATE TABLE IF NOT EXISTS`,
so there is nothing to detect drift against — an existing table simply predates the change. The
asymmetry is pre-existing and stated rather than fixed.

### 3. Read/write path — no changes

Every layer is already array-correct once the column type is right:

- **Write** — `json_populate_record` populates a `text[]` column from a JSON array.
- **Read** — `row_to_json(t)` serializes a `text[]` column as a JSON array.
- **Transport** — `JsonParser.Default.Parse<Struct>` maps that array to a `ListValue`.
- **Client** — `StructConverter.FromStruct<T>` formats the `Struct` back to JSON and hands it to
  `JsonSerializer.Deserialize<T>`, which binds a JSON array to `string[]` natively.

No serialization code changes anywhere. §4's round-trip test is what proves this empirically.

### 4. Client array detection

.NET already does this correctly and is unchanged. The other four gain it.

| Client | Mechanism |
|---|---|
| Java | `field.getGenericType()` — arrays via `getComponentType()`, `List<T>`/`Collection<T>` via the `ParameterizedType` argument |
| Python | `typing.get_type_hints(cls)` to resolve string annotations, then `typing.get_origin`/`get_args` |
| Go | `reflect.Slice` → `t.Elem()`, inside `goTypeToClr` (`registrar.go:173`) — see below, a signature change |
| TypeScript | New `@IversonArray(elementType)` decorator |

**Each must carve out its bytes type before the array unwrap**, mirroring `SchemaRegistrar.cs:241`:
Java `byte[]`, Python `bytes`, Go `[]byte`. Without it, a bytes field becomes an array of its
element type instead of the `ClrBytes` scalar it is today — a silent regression on working code.

**Python must resolve annotations before inspecting them.** `core.py:126-130` walks raw
`__annotations__`, which holds **strings** under `from __future__ import annotations` — the style
every existing Python entity uses (`sample/models.py:2`, `tests/test_schema_registrar.py:2`).
`typing.get_origin('list[str]')` returns `None`, so detection runs over `typing.get_type_hints(cls)`
instead. The existing scalar path survives raw strings only by accident: `_python_type_to_clr`
accepts `str | type` (`:54`) and `_PY_TO_CLR` keys on bare names (`:35-44`), so `'str'` hits the
`"str"` key while `'list[str]'` matches nothing.

Accepted cost: `get_type_hints` evaluates annotations in the defining module's namespace, so a
forward reference not importable at runtime — the `if TYPE_CHECKING:` relation-import pattern —
raises `NameError` where the raw walk did not. No `TYPE_CHECKING` usage exists in the Python client
today. Decision made by Ben 2026-08-02.

**Java needs a signature change, not just a branch.** `detectClrType(field.getType())`
(`SchemaRegistrar.java:176`, `:188`) receives a `Class<?>`, which erases generics — `List<String>`
arrives as `List`. Element-type recovery requires `getGenericType()`.

**Go needs a signature change too, not just a branch.** `goTypeToClr` returns a single
`pb.ClrType` (`registrar.go:173`) and its sole caller assigns that one value (`:69`); the
`PropertyDescriptor` literal (`:82-95`) has no `IsArray` field at all. A branch alone is a no-op —
the existing `reflect.Slice` arm already returns `CLR_STRING` for every non-`[]byte` slice
(`:186-189`), so `[]string` would register exactly as it does today. `goTypeToClr` returns
`(pb.ClrType, bool)`, the `reflect.Slice` arm recurses on `t.Elem()` with `isArray: true` after the
`[]byte` carve-out, and `:69` destructures both into `ClrType` and a new `IsArray` field on the
descriptor.

**TypeScript cannot infer the element type.** `Reflect.getMetadata('design:type', …)`
(`core.ts:235`) returns the `Array` constructor for `tags: string[]`; `emitDecoratorMetadata`
erases the element type, and the class *is* instantiated (`core.ts:228`) but an initialized `[]`
carries no element either. So TypeScript declares it explicitly:

```ts
@IversonArray(ClrType.CLR_STRING)
tags: string[] = [];
```

`@IversonArray` is a `PropertyDecorator`, matching the eight that already exist
(`annotations.ts:56-169`). Both `@IversonArray` and `ClrType` are added to `src/index.ts`'s
exports. `ClrType` is a generated proto enum and is not exported today — `index.ts` exposes the
twelve decorators, nine accessors, the builders and the three client classes, and no generated
type — so without it a consumer cannot name the decorator's argument. The registrar reads it for both `isArray` and `clrType`. A property whose
`design:type` is `Array` **without** the decorator is a registration error, not a silent
`CLR_STRING` — leaving it to the existing fallback would reproduce the silent-wrong-declaration
class this work exists to remove. Decision made by Ben 2026-08-02.

### 5. Testing

**Mapping completeness** — iterate `Enum.GetValues<ClrType>()`, asserting every value has an
`ArrayTypeOverrides` entry whose Postgres type is its scalar type plus `[]` and whose StarRocks type
is `STRING`. Payload kinds are asserted against an explicit expected table rather than derived from
the scalar map, carrying `ClrFloat → Keyword` as a named exception — §1 preserves it because
correcting it would retype a live Qdrant index. Enum-driven so a newly added `ClrType` fails here
rather than falling through.

**Normalization completeness** — iterate the same enum, asserting `NormalizePgType` answers for
every mapped SQL type, scalar and array.

**Drift, both directions** — a matching column is accepted silently; a differing one throws at
registration and logs at startup, with the message naming table, column, actual and expected. The
`TIMESTAMPTZ` **and `TIMESTAMPTZ[]`** cases are both asserted specifically, since they are the two
where `format_type`'s spelling differs and a naive comparison yields a false positive on a
*correct* column — and the scalar case passing is exactly what would mask the array case.

**Orphan-drop survives the query change** — a table that has had a column dropped still applies
cleanly, since the tombstone row is the case a hand-written filter is most likely to miss.

**Both DDL paths** — an array property on a newly registered type (`CREATE TABLE`) and an array
property added to an already-registered type (`ALTER TABLE ADD COLUMN`). Only the second exercises
`GetDefaultForType`, and a test against a fresh database takes only the first.

**Round-trip** — the test that would have caught this originally: an entity with a `string[]`
property persisted and read back with its elements intact, plus one numeric array. This is also
the empirical check on the Postgres semantics §3 reasons about.

**Client detection, per language** — an entity with an array property registers with `is_array` set
and the correct element `clr_type`; a bytes property still registers as the `ClrBytes` scalar. For
TypeScript, additionally: an `Array`-typed property without `@IversonArray` fails registration.

## Out of scope

Converting existing mis-typed columns — no `ALTER … USING`; the operator migrates by hand after the
drift error names the column. StarRocks `ARRAY<…>` representation and StarRocks drift detection.
Correcting `float[]`'s `Keyword` payload kind. Java's silent `CLR_STRING` fallback
(`SchemaRegistrar.java:177`) when type detection fails — real, same class, but its own change.

## Known issues — pre-existing, not addressed here

`ClrTypeToEngagementType` (`SchemaBuilder.cs:278-279`) defaults to `"STRING"` for any SQL type
absent from `SqlTypeMap`. With the table total over `ClrType` this default becomes unreachable for
declared types, but it remains as a silent fallback rather than an error.

## Verified assumptions

Verified against `main@5884b07`. B2, B7 and B11-B13 were executed against a live Postgres 16
instance during critical design review; B7 failed and is corrected below.

| # | Assumption | Evidence |
|---|---|---|
| B1 | `ArrayTypeOverrides` is the sole decider of array SQL types | Consumed only at `SchemaBuilder.cs:269` (`ClrTypeToSql`) and `:264` (`SqlTypeMap` construction) |
| B2 | **Executed.** Postgres has an array form of all nine scalar types | All 18 type strings resolve via `format_type(t::regtype, null)` against Postgres 16 |
| B3 | `ClrTypeMapping` is `(SqlType, StarRocksType, PayloadKind)` | `SchemaBuilder.cs:227` |
| B4 | Seven new array entries create no duplicate key in `SqlTypeMap` | `:262-265` — `ScalarTypeMap.Values.Concat(ArrayTypeOverrides.Values).ToDictionary(m => m.SqlType, …)`, which **throws on duplicates**. Post-change the 18 SQL-type strings are pairwise distinct (`TEXT` vs `TEXT[]`, etc.) |
| B5 | `BYTEA[]` is reachable only via `byte[][]` | `SchemaRegistrar.cs:241` returns `(ClrBytes, isArray: false)` for `byte[]` before the array unwrap |
| B6 | The existing-columns query can be extended to return types | `PostgresSchemaManager.cs:24-30` selects `column_name` from `information_schema.columns`; its result is used only as a local name `HashSet` |
| B7 | **Corrected and executed.** `format_type` matches 16 of the 18 post-change SQL type strings; **both** `TIMESTAMPTZ` and `TIMESTAMPTZ[]` differ | Run against Postgres 16: `SELECT t, format_type(t::regtype,null), lower(t) = format_type(t::regtype,null) FROM unnest(ARRAY[…]) t` → `TIMESTAMPTZ` → `timestamp with time zone` and `TIMESTAMPTZ[]` → `timestamp with time zone[]`, both `f`; the other 16 match |
| B8 | **Corrected.** `ApplySchemaAsync` has **five** production callers across **two** policy contexts | Registration: `SchemaRegistrationOrchestrator.cs:68`. Startup: `Program.cs:421` (`foreach (var descriptor in schemaRegistry.All.Values)`) plus three bootstrap calls at `:410-412` (`ReconciliationSchema`, `DlqSchema`, `TenantSchema`). `StartupNoOpFakes.cs:48` also implements the interface, and ~25 test call sites exist across five test files. The two-context split that drove §2's policy holds — all four `Program.cs` calls are startup — but the caller count does not |
| B9 | Nothing else consumes the existing-columns query shape | `PostgresSchemaManager.cs:25` — result is a local `HashSet<string>` used at `:51` and `:67` only |
| B10 | Registration surfaces a throw from the schema manager | `SchemaRegistrationOrchestrator.cs:68` awaits it inside the registration path with no catch around it |
| B11-B13 | **Executed.** The round-trip is array-correct at every layer | `OutboxWriter.cs:27` (`json_populate_record`), `EntityRepository.cs:9` (`row_to_json`), `ObjectMappingGrpcService.cs:81` (`JsonParser`), `StructConverter.cs:27-32` (`JsonSerializer.Deserialize<T>`). Run against Postgres 16: JSON arrays populate `text[]`/`integer[]`/`timestamptz[]`, and `row_to_json` returns `{"tags":["a","b"],"nums":[1,2],…}`. Negative control on a scalar `TEXT` column returns `{"tags":"[\"a\",\"b\"]"}` — the reported bug, reproduced |
| B14 | **Failed.** Four clients never emit `is_array` | Python `core.py:173` `is_array=False`; TypeScript `core.ts:249` `isArray: false`; Java `SchemaRegistrar.java` never sets it; Go `registrar.go` never sets it. Only .NET derives it. This drove §4 |
| B15 | No existing schema, sample, test or fixture declares an array property | Repo-wide grep over samples and `Iverson.LoadTest` returns nothing; no array fixture in any client test suite |
| B16 | Java can recover an element type | `detectClrType(field.getType())` at `:176`/`:188`/`:287` — `getType()` erases generics, so `getGenericType()` is required. Feasible, but a signature change |
| B17 | Python can recover an element type | `core.py:162-163` reads `__annotations__` and passes the hint to `_python_type_to_clr`; hints preserve `list[str]` for `typing.get_origin`/`get_args` |
| B18 | Go can recover an element type | `goTypeToClr(sf.Type)` (`registrar.go:69`, `:174`) already takes a `reflect.Type`; `reflect.Slice` → `Elem()` |
| B25 | **Go cannot propagate `isArray` without a signature change** | `goTypeToClr` returns a single `pb.ClrType` (`registrar.go:173`); its sole caller assigns that one value (`:69`), and the `PropertyDescriptor` literal (`:82-95`) has no `IsArray` field. `grep -rn "IsArray" Iverson.Clients/Go/iverson/*.go` → no matches. Element recovery (B18) is necessary but not sufficient |
| B19 | TypeScript supports a new per-property decorator | `annotations.ts:56-169` — eight existing `PropertyDecorator`s establish the pattern |
| B20 | TypeScript cannot infer the element type | `core.ts:235` reads `design:type`, which `emitDecoratorMetadata` erases to `Array`; `core.ts:228` instantiates the class but an initialized `[]` carries no element type |
| B21 | StarRocks accepts `STRING` for array columns | `SchemaBuilder.cs:191-193` builds `EngagementColumnSchema` from `ClrTypeToEngagementType(SqlType)`, already `STRING` for both existing arrays |
| B22 | StarRocks has no `ALTER` path | `StarRocksSchemaManager.cs:16` is `CREATE TABLE IF NOT EXISTS`; no `ALTER TABLE` anywhere in `Iverson.StarRocks` |
| B23 | The existing-columns query has **two** consumers, and changing its source affects both | `PostgresSchemaManager.cs:51` (ADD) and `:67` (orphan DROP) both read `existingColumns`. Executed against Postgres 16: an unfiltered `pg_attribute` query returns 6 system columns plus dropped tombstones, and `DROP COLUMN IF EXISTS "ctid"` errors |
| B24 | Adding a non-nullable column to an existing table depends on `GetDefaultForType` | `PostgresSchemaManager.cs:55` invokes it for every non-nullable added column; `:146-157` has no array case. Executed: `ADD COLUMN "tags" TEXT[] NOT NULL DEFAULT ('')` → `ERROR: malformed array literal: ""` |
