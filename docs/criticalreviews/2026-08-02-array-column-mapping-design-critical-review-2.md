# Critical Design Review: 2026-08-02-array-column-mapping-design (Round 2)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-02-array-column-mapping-design.md`
**Verified Assumptions section:** present (22 rows)

Coverage re-derived from the amended spec before consulting round 1. The live Postgres 16 container
was used again; all three findings below are reproduced with actual error output rather than
reasoned.

## 0. Coverage enumeration

### Sections

| Row | Disposition |
|---|---|
| Problem | ok — re-read against round 1's executed evidence; the `string[]`-broken-read and `int[]`-failed-write distinction is recorded and unchanged |
| Goal | ok — three clauses, each still traceable to a section |
| Design §1 (mappings) | ok — 18 SQL strings re-confirmed to resolve; `SqlTypeMap` duplicate-key hazard re-checked |
| **Design §2 (drift detection)** | **→ §2.1 — the prescribed `pg_attribute` query breaks the pre-existing orphan-drop path** |
| Design §3 (read/write) | ok — round-trip re-run; `text[]`/`integer[]`/`timestamptz[]` populate and serialize correctly |
| **Design §4 (client detection)** | **→ §2.3 (TypeScript).** Java, Python, Go rows checked clean — see rules below |
| Design §5 (testing) | → §2.2 — no test covers the `ADD COLUMN` path, which is where every array type fails |
| Out of scope | ok — five exclusions; "no `ALTER … USING`" is consistent with §2's throw-and-log policy |
| Known issues | ok — `ClrTypeToEngagementType`'s `"STRING"` default re-read at `:278-279` |
| Verified assumptions | ok — see §1 |

### Rules and operands

| Row | Disposition |
|---|---|
| `ClrTypeToSql` array branch | ok — post-change total over `ClrType`; fallthrough unreachable for arrays |
| Drift comparison — false positives | ok — round 1's `TIMESTAMPTZ[]` fix verified present in the amended §2 and §5 |
| Drift comparison — false negatives | ok — `format_type` exact for the other 16; a differing type cannot compare equal |
| **Existing-column enumeration — over-inclusion** | **→ §2.1** — `pg_attribute` returns system and dropped columns the current `information_schema` query does not |
| Existing-column enumeration — under-inclusion | ok — `pg_attribute` filtered to `attnum > 0 AND NOT attisdropped` returns exactly the user columns `information_schema.columns` does |
| **`NOT NULL` default generation for new columns** | **→ §2.2** — `GetDefaultForType` has no array case and every branch produces a scalar literal |
| Bytes carve-out — Java | ok — `SchemaRegistrar.java:297` maps `byte[].class → CLR_BYTES`; an array unwrap placed before it would capture `byte[]` and break this. The spec's ordering mandate is correctly grounded |
| Bytes carve-out — Go | ok — `goTypeToClr` takes `reflect.Type` (`registrar.go:174`); `[]byte` is `reflect.Slice`, so the same ordering hazard applies and the spec names it |
| Bytes carve-out — Python | ok — `_PY_TO_CLR` keys `"bytes"` (`core.py:43`); `bytes` is not a parameterized generic so `get_origin` returns `None` and it cannot be mistaken for a list |
| Payload-kind reachability | ok — `ToCollectionSchema` (`:222`) enumerates `ScalarColumns`, which includes arrays |

### Data-flow arrows

| Row | Disposition |
|---|---|
| client → `is_array` → `SchemaBuilder` | ok — the arrow §4 creates; Python's mechanism corrected in round 1 |
| `SqlType` → `CREATE TABLE` *(new table)* | ok — executed: `"tags" TEXT[] NOT NULL` is valid DDL |
| **`SqlType` → `ALTER TABLE ADD COLUMN` *(existing table)*** | **→ §2.2** — executed and fails for every array type |
| existing columns → orphan `DROP COLUMN` | **→ §2.1** — executed and fails once the query source changes |
| JSON → `json_populate_record` → array column *(persistence boundary)* | ok — re-executed, all three array types |
| array column → `row_to_json` → `ListValue` *(persistence boundary)* | ok — re-executed |
| array value → StarRocks INSERT | ok — `JsonElementToObject`'s `JsonValueKind.Array => el.GetRawText()` arm (`:521-529`) |
| **`ClrType` → TypeScript decorator argument** | **→ §2.3** — the type the decorator's signature names is not in the package's public exports |

## 1. Verified-assumptions cross-check

All 22 reconfirmed on a fresh read, including round 1's three corrections (B2, B7, B11-B13 now
recorded as executed, with B7's two-mismatch result). Spot-checks: B1 (`:264`, `:269`), B4
(`ToDictionary` duplicate-key hazard, 18 distinct strings), B5 (`SchemaRegistrar.cs:241`), B8 (both
call sites), B14 (four client sites), B16 (`SchemaRegistrar.java:287-301`), B21, B22.

**Span check — two uncovered dependencies, both escalated:**

1. **`PostgresSchemaManager`'s orphan-drop loop depends on the existing-columns query returning
   only user columns.** §2 changes that query's source and no assumption covers the consequence.
   B6 states the query "can be extended to return types" — true as scoped, and silent about what
   else the new source returns. Verified in-round; escalated to §2.1.
2. **Adding an array column to an existing table depends on `GetDefaultForType`.** No assumption
   mentions it; `PostgresSchemaManager.cs:55` invokes it for every non-nullable added column.
   Verified in-round; escalated to §2.2.

Both live in the gap between listed items rather than contradicting one — B6 is accurate about the
half it describes, and nothing describes the other half.

## 2. Literal-wrongness findings

### §2.1 — The prescribed `pg_attribute` query breaks the orphan-drop path on every existing table

**Description.** §2 says the existing-columns query "is extended to return
`(name, format_type(atttypid, atttypmod))` from `pg_attribute`", with no filter stated.

`information_schema.columns` — the current source (`PostgresSchemaManager.cs:25-30`) — returns only
user columns. `pg_attribute` does not. It also returns the six system columns and any dropped
column's tombstone:

```
           attname            | attnum | attisdropped |  typ
------------------------------+--------+--------------+--------
 tableoid                     |     -6 | f            | oid
 cmax                         |     -5 | f            | cid
 xmax                         |     -4 | f            | xid
 cmin                         |     -3 | f            | cid
 xmin                         |     -2 | f            | xid
 ctid                         |     -1 | f            | tid
 ........pg.dropped.1........ |      1 | t            | -
 tags                         |      2 | f            | text[]
```

That result feeds the **same `existingColumns` variable** the pre-existing orphan-drop loop reads
(`:67`): `existingColumns.Where(c => !schemaColumnNames.Contains(c))`. System columns are in no
schema, so every one becomes an "orphan" and the loop issues `DROP COLUMN` for it. `IF EXISTS` does
not save it:

```
ALTER TABLE drop_probe DROP COLUMN IF EXISTS "ctid";
ERROR:  cannot drop system column "ctid"
```

`ApplySchemaAsync` would therefore throw for **every existing table** — at registration
(`SchemaRegistrationOrchestrator.cs:68`) and at startup (`Program.cs:421`), where §2's own policy
says startup must only log. The drift check itself is unaffected (it compares only names present in
`schema.Columns`), so this is collateral damage to working code, not a flaw in the new logic.

**Evidence.**
- Both queries run against Postgres 16; output above.
- `PostgresSchemaManager.cs:25-30` (current `information_schema` query), `:51` and `:67` (the two
  consumers of `existingColumns`).
- Spec §2, "The query is extended to return `(name, format_type(atttypid, atttypmod))` from
  `pg_attribute`."

**Proposed fix.** State the filter in the spec: the `pg_attribute` query must carry
`AND a.attnum > 0 AND NOT a.attisdropped`, which restricts it to exactly the user columns
`information_schema.columns` returns today. Add a test asserting the orphan-drop path still works on
a table that has had a column dropped, since the tombstone row is the case a hand-written filter is
most likely to miss.

### §2.2 — Every array type fails when added to an existing table

**Description.** §5 specifies a round-trip test and drift tests, but nothing covers adding an array
column to a table that already exists. That path runs `PostgresSchemaManager.cs:53-56`:

```csharp
ADD COLUMN IF NOT EXISTS "{col.Name}" {col.SqlType}{(col.IsNullable ? "" : $" NOT NULL DEFAULT ('{GetDefaultForType(col.SqlType)}')")}
```

`GetDefaultForType` (`:146-157`) matches on scalar type-name prefixes and has no array case. Every
array type therefore receives a scalar literal, and every one is a malformed array literal:

| Column type | Branch matched | Default emitted | Result |
|---|---|---|---|
| `TEXT[]`, `BYTEA[]` | `_` | `''` | malformed array literal |
| `INTEGER[]`, `BIGINT[]` | `StartsWith("INT")` | `'0'` | malformed array literal |
| `REAL[]` | `StartsWith("REAL")` | `'0'` | malformed |
| `DOUBLE PRECISION[]` | `StartsWith("DOUBLE")` | `'0'` | malformed |
| `BOOLEAN[]` | `StartsWith("BOOL")` | `'false'` | malformed |
| `UUID[]` | `StartsWith("UUID")` | `'00000000-…'` | malformed |
| `TIMESTAMPTZ[]` | `StartsWith("TIMESTAMP")` | `'1970-01-01 00:00:00+00'` | malformed |

Executed:

```
ALTER TABLE def_probe ADD COLUMN IF NOT EXISTS "tags" TEXT[] NOT NULL DEFAULT ('');
ERROR:  malformed array literal: ""
DETAIL:  Array value must start with "{" or dimension information.
```

This is the common path, not an edge case: `CREATE TABLE` emits no default and works, so a fresh
type succeeds while **adding an array property to an existing type fails**. And the non-nullable
branch is the default one — .NET's `SchemaRegistrar.cs:243-264` returns `isNullable: false` for a
plain `string[] Tags`, since `isNullable` is set only by a `Nullable<T>` unwrap.

§5's round-trip test would not catch it: a test entity registered against a fresh database takes the
`CREATE TABLE` path.

**Evidence.**
- `PostgresSchemaManager.cs:53-56` (the `ADD COLUMN` template), `:146-157` (`GetDefaultForType`).
- `SchemaRegistrar.cs:243-264` — `isNullable` derives from `Nullable<T>` only.
- Executed error above.

**Proposed fix.** `GetDefaultForType` needs an array case returning `'{}'` — the empty-array
literal, valid for every Postgres array type — checked **before** the scalar prefix branches, since
`INTEGER[]` matches `StartsWith("INT")` today. Add to §5 a test that registers a type, then
re-registers it with an array property added, asserting the `ALTER TABLE` path succeeds. That test
is what distinguishes the two DDL paths, which no current test does.

### §2.3 — TypeScript's decorator names a type the package does not export

**Description.** §4 specifies the TypeScript declaration as:

```ts
@IversonArray(ClrType.CLR_STRING)
tags: string[] = [];
```

`ClrType` is a generated protobuf enum and is **not exported** from the package's public entry
point. `src/index.ts` exports the twelve decorators, nine accessor functions, four builder modules,
the three client classes and four type aliases — and no `ClrType`. A consumer of `@iverson/client`
cannot write the line §4 specifies without importing from the generated output directly, which is
not part of the package's surface.

This matters because §4 exists specifically to make arrays declarable in TypeScript; a declaration
form whose argument cannot be named is not declarable.

**Evidence.**
- `Iverson.Clients/TypeScript/src/index.ts` — full export list read; `ClrType` absent.
- Spec §4, TypeScript row and the code block.

**Proposed fix.** The smallest change is to add `ClrType` to `index.ts`'s exports alongside the
decorator itself, and say so in §4.

The alternative worth weighing inline: give `@IversonArray` a string-literal-union parameter
(`'string' | 'int' | 'long' | 'float' | 'double' | 'bool' | 'datetime' | 'guid' | 'bytes'`) mapped
internally to `ClrType`. That keeps generated proto types out of the public API — consistent with
the package's current surface, which exposes no generated type — and reads more idiomatically in
TypeScript, at the cost of a second name for a concept the other four clients express with their
native type system. Either resolves the finding; the export is smaller, the union is more consistent
with what `index.ts` currently chooses to expose.

## 3. Forced decisions

No forced decisions found.

§2.3 carries an either/or, but it is resolved by its own proposed fix rather than blocked on a
constraint the spec cannot see — both options are available and the smaller one is identified. That
is a §2 fix with an inline alternative, not a §3.

## 4. Previously addressed

- **Round 1 §2.1** (normalization count) — resolved. §2 now reads "16 of the 18 post-change
  strings" and names both `TIMESTAMPTZ` and `TIMESTAMPTZ[]`; §5 asserts both cases, with the
  rationale that the scalar passing is what would mask the array.
- **Round 1 §2.2** (Python mechanism) — resolved. §4's Python row is now
  `typing.get_type_hints(cls)`, with a note explaining why raw `__annotations__` fails and citing
  the two entity files that use `from __future__ import annotations`.
- **Round 1 §3.1** (annotation resolution) — resolved by Ben's pick of `get_type_hints`, with the
  accepted `NameError` cost recorded in §4 rather than left implicit.
- **Round 1 §1 (B7 failed)** — resolved. The row now carries the executed query and both mismatches.
- **Round 1's "reasoned" caveats** — resolved. B2 and B11-B13 record executed results, and the
  section preamble no longer claims anything is unverified.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes**

§3 is empty. §2 carries three findings, none of which changes the design's shape — the mapping
table, the drift-detection split, the read/write conclusion and all four client mechanisms stand.

Two of the three are in the same place and share a cause: §2's design was specified against the
drift check it adds, without tracing what the query change does to the two pre-existing consumers
of the same variable, or what the `ADD COLUMN` template does with a type it has never seen. Both
break working code rather than the new feature, and both fail on every existing table rather than
in an edge case. §2.3 is smaller but blocks the TypeScript half of §4 outright.
