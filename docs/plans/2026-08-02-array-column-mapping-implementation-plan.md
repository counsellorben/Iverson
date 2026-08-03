# Array Column Mapping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-02-array-column-mapping-design.md` (commit SHA: `93b58ff`)

**Goal:** Arrays that work end to end — declarable in all five clients, stored in a real Postgres array column, round-tripping intact, with any pre-existing column whose type disagrees reported rather than ignored.

**Architecture:** The server's `ArrayTypeOverrides` table becomes total over `ClrType`, so every array element type maps to a real Postgres array column instead of falling through to the scalar map. `PostgresSchemaManager` gains type-aware schema reading and compares the live column type to the registry's, throwing at registration and logging at startup. The four clients that never emit `is_array` learn to detect arrays and send the element type; .NET already does and is unchanged.

**Tech stack:** .NET (Iverson.Api / Iverson.Sql), Postgres 16, Npgsql + Dapper, xUnit + FluentAssertions + NSubstitute + Testcontainers; clients in Java (Maven/JUnit), Python (pytest), Go (`go test`), TypeScript (vitest).

---

## Global Constraints

Copied from the spec; every task holds to these.

- **StarRocks stays `STRING` for every array.** `StarRocksSchemaManager` is `CREATE TABLE IF NOT EXISTS` only with no `ALTER` anywhere in the project, so any other representation has no migration path.
- **Payload kinds stay element-typed**, so an `Integer` index over an integer list stays range-filterable — **except** `ClrFloat`, whose existing `Keyword` is preserved rather than corrected, because changing it would retype a live Qdrant index. `ClrGuid` is `Keyword` on both sides already.
- **Each client carves out its bytes type before the array unwrap.** Java `byte[]`, Python `bytes`, Go `[]byte`. Without it a bytes field becomes an array of its element type instead of the `ClrBytes` scalar it is today — a silent regression on working code.
- **No proto change.** `is_array` already exists on `PropertyDescriptor`; this work populates it.
- **Registration throws on drift; startup logs.** Throwing at startup would turn any historical drift into a boot failure.

## File Structure

**Modify — server**
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:249-255` — `ArrayTypeOverrides` total over `ClrType`
- `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs` — `pg_attribute` query (`:25-31`), drift comparison, `NormalizePgType`, `GetDefaultForType` array case (`:146-156`)
- `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:12` — optional `SchemaDriftPolicy` parameter on `ApplySchemaAsync`
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:68` — passes `SchemaDriftPolicy.Throw`
- `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:48` — fake implementer restates the new parameter

**Modify — clients**
- `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java` — `detectClrType` overload taking `Type`, `setIsArray`
- `Iverson.Clients/Python/iverson_client/core.py` — `get_type_hints`, `_python_type_to_clr` returns `(clr, is_array)`
- `Iverson.Clients/Go/iverson/registrar.go` — `goTypeToClr` returns `(pb.ClrType, bool)`, descriptor gains `IsArray`
- `Iverson.Clients/TypeScript/src/annotations.ts`, `src/core.ts`, `src/index.ts` — `@IversonArray`

**Test**
- `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs` — mapping + normalization completeness
- `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs` — drift both directions, orphan-drop, both DDL paths, round-trip
- `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`
- `Iverson.Clients/Python/tests/test_schema_registrar.py`
- `Iverson.Clients/Go/iverson_test/registrar_test.go`
- `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here:

- **B1** `ArrayTypeOverrides` is the sole decider of array SQL types — consumed only at `SchemaBuilder.cs:269` and `:264`.
- **B2** *(executed)* Postgres has an array form of all nine scalar types — all 18 type strings resolve via `format_type` against Postgres 16.
- **B3** `ClrTypeMapping` is `(SqlType, StarRocksType, PayloadKind)` — `SchemaBuilder.cs:227`.
- **B4** Seven new array entries create no duplicate key in `SqlTypeMap` — the 18 SQL-type strings are pairwise distinct.
- **B5** `BYTEA[]` is reachable only via `byte[][]` — `SchemaRegistrar.cs:241`.
- **B6, B9** The existing-columns query can be extended to return types; nothing else consumes its shape.
- **B7** *(executed, corrected)* `format_type` matches 16 of 18; **both** `TIMESTAMPTZ` and `TIMESTAMPTZ[]` differ.
- **B8** *(corrected)* `ApplySchemaAsync` has five production callers across two policy contexts.
- **B10** Registration surfaces a throw from the schema manager — `SchemaRegistrationOrchestrator.cs:68`.
- **B11-B13** *(executed)* The round-trip is array-correct at every layer; the scalar-`TEXT` negative control reproduces the reported bug.
- **B14** *(failed → drove §4)* Four clients never emit `is_array`.
- **B15** No existing schema, sample, test or fixture declares an array property.
- **B16-B20** Java/Python/Go can recover an element type; TypeScript cannot and needs an explicit decorator.
- **B21, B22** StarRocks accepts `STRING` for arrays and has no `ALTER` path.
- **B23** *(executed)* The existing-columns query has two consumers; an unfiltered `pg_attribute` query returns system columns and tombstones, and `DROP COLUMN IF EXISTS "ctid"` errors.
- **B24** *(executed)* `ADD COLUMN … TEXT[] NOT NULL DEFAULT ('')` → `ERROR: malformed array literal`.
- **B25** Go cannot propagate `isArray` without a signature change.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `main@93b58ff`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | All six server files exist at the cited paths | `SchemaBuilder.cs`, `PostgresSchemaManager.cs`, `IRecordStoreRoles.cs`, `SchemaRegistrationOrchestrator.cs`, `SchemaBuilderTests.cs`, `PostgresIntegrationTests.cs` all read directly |
| P2 | File path | All four client source files and their four test files exist | `find`/`ls` over each client tree; test files are `SchemaRegistrarTest.java`, `test_schema_registrar.py`, `iverson_test/registrar_test.go`, `tests/schema-registrar.test.ts` |
| P3 | Signature | `ApplySchemaAsync` is `Task ApplySchemaAsync(TableSchema schema)` on both interface and impl | `IRecordStoreRoles.cs:12`, `PostgresSchemaManager.cs:14` |
| P4 | **Consumer impact** | **`NoOpRecordStoreSchemaManager` must be updated.** C# requires an implementer to restate a defaulted interface parameter; the fake will not satisfy the new signature otherwise | `StartupNoOpFakes.cs:46` (`internal sealed class NoOpRecordStoreSchemaManager : IRecordStoreSchemaManager`), `:48` (`public Task ApplySchemaAsync(TableSchema schema) => Task.CompletedTask;`). Registered at `AuthTestWebApplicationFactory.cs:58` |
| P5 | **Consumer impact** | The optional-parameter approach leaves all other call sites compiling untouched | 5 production sites (`Program.cs:410,411,412,421`, `SchemaRegistrationOrchestrator.cs:68`) and ~25 test sites across 5 files; `PostgresSchemaManager` is the only concrete implementer (`ServiceCollectionExtensions.cs:23`). NSubstitute `Substitute.For<IRecordStoreSchemaManager>()` uses a dynamic proxy and needs no source change |
| P27 | Consumer impact | **A non-`RpcException` thrown below the gRPC layer loses its message** | `ObjectMappingGrpcService.cs:37-57` — `RegisterSchema` has no try/catch around `:47`; `ActingUserInterceptor.cs` has no `catch`; `Program.cs:87` configures `AddGrpc` without `EnableDetailedErrors`. The client receives `StatusCode.Unknown` / `"Exception was thrown by handler."`. Every other registration failure throws `RpcException` explicitly (`SchemaRegistrationOrchestrator.cs:45-47,62-64,87-90`) |
| P6 | Signature | `GetDefaultForType` is a switch expression over `sqlType.ToUpperInvariant()` with `var t when t.StartsWith(...)` arms, evaluated top-down | `PostgresSchemaManager.cs:146-156`. An `EndsWith("[]")` arm placed first therefore captures `INTEGER[]` before `StartsWith("INT")` |
| P7 | Signature | Java `detectClrType(Class<?>)` is `private static`, returns `ClrType` or `null`, with exactly **two** call sites | `SchemaRegistrar.java:287` (definition), `:176` (`buildKeyDescriptor`), `:188` (`tryBuildPropertyDescriptor`) |
| P8 | Consumer impact | Java currently **drops** array fields rather than mis-typing them | `detectClrType` returns `null` for `List`/`Collection` (`:287-299`, no matching branch); `tryBuildPropertyDescriptor` returns `null` on that and the field is skipped (`:188-189`). Task 3 turns a dropped field into a registered one |
| P9 | Signature | Go `goTypeToClr(t reflect.Type) pb.ClrType` has exactly **one** call site | Definition at `registrar.go:174` (**not** `:173`, which is its doc comment — the spec's citation is off by one); sole call at `:69` (`clrType := goTypeToClr(sf.Type)`) |
| P10 | Signature | Go already carves out `[]byte` and returns `CLR_STRING` for every other slice | `registrar.go:186-189`. The carve-out is preserved, not added; the `CLR_STRING` fallthrough is what gets replaced |
| P11 | Signature | Python `_python_type_to_clr(type_hint: str \| type \| None) -> int` has exactly **one** call site | Definition `core.py:54`, call `core.py:163` |
| P12 | Consumer impact | The `get_type_hints` switch breaks no existing Python entity | No `TYPE_CHECKING` usage anywhere in the Python client; `sample/models.py` and all test entities use `from __future__ import annotations`, and every annotation resolves at call time (`Author.articles: list` is a bare builtin and is a relation field, skipped before the type path) |
| P25 | Signature | **`core.py` binds only selected `typing` names, not the module** | `core.py:9` — `from typing import Generic, List, Optional, TypeVar`; no `import typing` anywhere in the file. Any `typing.<name>` reference is unbound until the import is extended |
| P13 | Consumer impact | TypeScript's new "undecorated `Array` property throws" breaks no existing fixture | No decorated entity class in `tests/` or `sample/` declares an array-typed field; the `[]` hits in `core.test.ts` are local variables and response arrays, not entity properties |
| P14 | Signature | TypeScript property decorators use `Reflect.defineMetadata(KEY, value, target.constructor)` and a paired getter | `annotations.ts:68-75` (`IversonKey`/`getKeyField`), the pattern `@IversonArray`/`getArrayFields` follows |
| P15 | Consumer impact | `ClrType` is genuinely absent from TypeScript's public exports | `src/index.ts` exports 12 decorators, the accessors, builders and client classes, and no generated proto type |
| P26 | Signature | **`annotations.ts` imports no generated proto type, and can safely gain one** | `annotations.ts:20` — sole import is `import 'reflect-metadata';`. `ClrType` originates at `../generated/object_mapping.js` (`core.ts:9,18`). No cycle: `annotations.ts` imports nothing from `core.ts` |
| P28 | Signature | **`core.ts` imports its annotation accessors by explicit name** | `core.ts:44-62` — a named-import block from `./annotations.js` listing `getChunkFields`, `getEmbeddingFields`, `getKeyField`, `getLargeFields`, `getMetadataFields`, `getRelations`, `getSearchKeys`, `getTenantFields` and the rest. A new accessor is unbound until added to that list |
| P16 | Signature | `PostgresContainerFixture` exposes `SchemaManager` and `ConnectionString` and is consumed via `IClassFixture` | `PostgresIntegrationTests.cs:9-38`; `postgres:16-alpine`, `UniqueTable()` helper at `:44-45` |
| P17 | Command | `dotnet test <project-dir>` is the invocation — there is no solution file | No `.sln` at repo root or under `Iverson.Server/`; each test project has its own `.csproj` |
| P18 | Command | `mvn -f Iverson.Clients/Java/pom.xml test` is valid | `pom.xml` declares modules `client` and `sample` |
| P19 | Command | `pytest` run from `Iverson.Clients/Python` is valid | `pyproject.toml` `[tool.pytest.ini_options] testpaths = ["tests"]` |
| P20 | Command | `go test ./...` run from `Iverson.Clients/Go` is valid | `go.mod` module `github.com/iverson/clients/go`, go 1.25.0; packages `iverson`, `iverson_test` |
| P21 | Command | `npm test` in `Iverson.Clients/TypeScript` runs vitest | `package.json` `scripts.test` = `"vitest run"` |
| P22 | Ordering | Task 2's drift check must not reject Task 1's new array types, so Task 1 lands first | Task 2's comparison reads `ColumnDescriptor.SqlType`, which Task 1 defines for arrays; a drift test written against `TEXT[]` fails if `ArrayTypeOverrides` still yields `TEXT` |
| P23 | Ordering | Tasks 3-6 are mutually independent and independent of Tasks 1-2 | Each touches one client tree only; `is_array` is a pre-existing proto field, so no client depends on the server change to compile or test |
| P24 | Ordering | Task 7 depends on Tasks 1 and 2 only | It asserts array DDL and round-trip against a real container; no client code is involved |

## Tasks

### Task 1: Complete the server's array type mappings

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:249-255`
- Modify: `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs:146-156`
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`

**Interfaces — Produces:** array `SqlType` strings (`TEXT[]`, `INTEGER[]`, …) that Task 2's drift comparison and Task 7's DDL assertions both consume.

- [ ] **Step 1: Make `ArrayTypeOverrides` total over `ClrType`.**
Replace the two-entry dictionary. StarRocks is `STRING` for every row; payload kinds are element-typed except `ClrFloat`, which keeps `Keyword`.

```csharp
    private static readonly IReadOnlyDictionary<ClrType, ClrTypeMapping> ArrayTypeOverrides =
        new Dictionary<ClrType, ClrTypeMapping>
        {
            [ClrType.ClrGuid]     = new("UUID[]", "STRING", PayloadIndexKind.Keyword),
            [ClrType.ClrString]   = new("TEXT[]", "STRING", PayloadIndexKind.Keyword),
            [ClrType.ClrInt32]    = new("INTEGER[]", "STRING", PayloadIndexKind.Integer),
            [ClrType.ClrInt64]    = new("BIGINT[]", "STRING", PayloadIndexKind.Integer),
            // Keyword, not Float: preserved from the pre-existing entry because changing it
            // would retype a live Qdrant index. See the spec's §1 and "Out of scope".
            [ClrType.ClrFloat]    = new("REAL[]", "STRING", PayloadIndexKind.Keyword),
            [ClrType.ClrDouble]   = new("DOUBLE PRECISION[]", "STRING", PayloadIndexKind.Float),
            [ClrType.ClrBool]     = new("BOOLEAN[]", "STRING", PayloadIndexKind.Boolean),
            [ClrType.ClrDatetime] = new("TIMESTAMPTZ[]", "STRING", PayloadIndexKind.Datetime),
            // Reachable only via byte[][] — byte[] is carved out as a scalar at
            // SchemaRegistrar.cs:241. Present so the table is total over the enum.
            [ClrType.ClrBytes]    = new("BYTEA[]", "STRING", PayloadIndexKind.Keyword)
        };
```

- [ ] **Step 2: Give `GetDefaultForType` an array case, above the scalar prefix arms.**
`'{}'` is a valid empty literal for every Postgres array type. It must precede the `StartsWith` arms, which would otherwise capture `INTEGER[]` via `StartsWith("INT")` and emit `'0'`.

```csharp
    private static string GetDefaultForType(string sqlType) => sqlType.ToUpperInvariant() switch
    {
        var t when t.EndsWith("[]")          => "{}",
        var t when t.StartsWith("INT")       => "0",
        var t when t.StartsWith("FLOAT")     => "0",
        var t when t.StartsWith("REAL")      => "0",
        var t when t.StartsWith("DOUBLE")    => "0",
        var t when t.StartsWith("BOOL")      => "false",
        var t when t.StartsWith("UUID")      => "00000000-0000-0000-0000-000000000000",
        var t when t.StartsWith("TIMESTAMP") => "1970-01-01 00:00:00+00",
        _                                    => ""
    };
```

- [ ] **Step 3: Add the mapping-completeness test.**
Iterate `Enum.GetValues<ClrType>()`; assert every value has an `ArrayTypeOverrides` entry whose Postgres type is its scalar type plus `[]` and whose StarRocks type is `STRING`. Assert payload kinds against an explicit expected table that carries `ClrFloat → Keyword` as a named exception — do **not** derive them from the scalar map, which would fail on exactly that row. Enum-driven so a newly added `ClrType` fails here rather than falling through.

- [ ] **Step 4: Run the tests.**
```bash
dotnet test Iverson.Server/Iverson.Api.Tests
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs
git commit -m "complete array type mappings for all nine ClrType values"
```

---

### Task 2: Schema-drift detection

**Files:**
- Modify: `Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs:24-31,51,66-72`
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs:12`
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs:68`
- Modify: `Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs:48`
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs`

**Interfaces — Consumes:** Task 1's array `SqlType` strings.

- [ ] **Step 1: Add `SchemaDriftPolicy` and thread it through as an optional parameter.**
In `IRecordStoreRoles.cs`, alongside the interface:

```csharp
public enum SchemaDriftPolicy
{
    /// Log a warning and continue — startup, where a boot failure on historical drift is worse.
    Warn,
    /// Throw — registration, where a RegisterSchema depending on a mis-typed column must fail.
    Throw
}

public interface IRecordStoreSchemaManager
{
    Task ApplySchemaAsync(TableSchema schema, SchemaDriftPolicy driftPolicy = SchemaDriftPolicy.Warn);
    Task EnsureRuntimeRoleAsync();
}
```

Declare the drift exception alongside the enum, so the detail survives to the caller (see Step 5):

```csharp
public sealed class SchemaDriftException(string table, string column, string actual, string expected)
    : Exception($"Column \"{column}\" on table \"{table}\" has type '{actual}' but the registered schema expects '{expected}'. Migrate the column by hand, then retry registration.");
```

The default keeps all five production and ~25 test call sites compiling unchanged. **`NoOpRecordStoreSchemaManager` must still be updated** — C# requires an implementer to restate the parameter:

```csharp
    public Task ApplySchemaAsync(
        TableSchema schema,
        SchemaDriftPolicy driftPolicy = SchemaDriftPolicy.Warn) => Task.CompletedTask;
```

- [ ] **Step 2: Read column types, not just names.**
Replace the `information_schema.columns` query. The `attnum > 0 AND NOT attisdropped` filter is **not optional** — `pg_attribute` also returns the six system columns and dropped-column tombstones, and the same result feeds the orphan-`DROP` loop at `:66`, which would then attempt `DROP COLUMN "ctid"` on every existing table. `IF EXISTS` does not suppress it.

```csharp
var existingColumns = (await conn.QueryAsync<(string Name, string Type)>(
    """
    SELECT a.attname AS name, format_type(a.atttypid, a.atttypmod) AS type
    FROM pg_attribute a
    JOIN pg_class c ON c.oid = a.attrelid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relname = @TableName
      AND a.attnum > 0 AND NOT a.attisdropped
    """,
    new { schema.TableName })).ToList();
```

Keep a name-only `HashSet<string>` (`OrdinalIgnoreCase`) derived from this for the existing ADD filter (`:51`) and orphan-DROP loop (`:66`) so their behaviour is unchanged.

- [ ] **Step 3: Add `NormalizePgType`.**
`format_type` returns Postgres's canonical spelling, matching our `SqlType` strings case-insensitively for 16 of 18. Both `TIMESTAMPTZ` and `TIMESTAMPTZ[]` differ.

```csharp
private static string NormalizePgType(string sqlType) => sqlType.Trim().ToLowerInvariant() switch
{
    "timestamptz"   => "timestamp with time zone",
    "timestamptz[]" => "timestamp with time zone[]",
    var t           => t
};
```

- [ ] **Step 4: Compare types over the name intersection.**
For every schema column whose name already exists, compare `NormalizePgType(expected.SqlType)` to `NormalizePgType(actual)`. New columns still ADD; orphans still DROP; only the intersection is checked. **Include the key column** — on an existing table it was created with the table and never revisited, so it must be appended to the checked set explicitly (`schema.Columns` excludes it; see `:57`). On mismatch, `Throw` raises a `SchemaDriftException` carrying table, column, actual and expected; `Warn` logs the same detail.

- [ ] **Step 5: Registration opts into throwing.**
`SchemaRegistrationOrchestrator.cs:68` becomes:

```csharp
try
{
    await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor), SchemaDriftPolicy.Throw);
}
catch (SchemaDriftException ex)
{
    throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
}
```

The `try/catch` is not optional: `Iverson.Sql` has no gRPC dependency, so a bare `SchemaDriftException` reaches the client as `StatusCode.Unknown` / `"Exception was thrown by handler."` and the table, column, actual and expected detail is lost. This matches how every other failure in this file surfaces. The four `Program.cs` calls (`:410-412` bootstrap, `:421` registered descriptors) are all startup and keep the `Warn` default, which logs and never throws.

- [ ] **Step 6: Add the tests to `PostgresIntegrationTests`.**
Using the existing `PostgresContainerFixture` and `UniqueTable()`:
  - A matching column is accepted silently.
  - A differing column throws under `Throw` and logs under `Warn`, message naming table, column, actual, expected.
  - **`TIMESTAMPTZ` and `TIMESTAMPTZ[]` are both asserted as non-drift** — these are the two where `format_type`'s spelling differs, a naive comparison yields a false positive on a *correct* column, and the scalar case passing is exactly what would mask the array case.
  - Normalization completeness: iterate `Enum.GetValues<ClrType>()` asserting `NormalizePgType` answers for every mapped SQL type, scalar and array.
  - Orphan-drop still applies cleanly on a table that has had a column dropped (the tombstone row).
  - `ALTER TABLE ADD COLUMN` of an array on an already-registered type succeeds — the only path that exercises `GetDefaultForType`.

- [ ] **Step 7: Run the tests.**
```bash
dotnet test Iverson.Server/Iverson.Sql.Tests
dotnet test Iverson.Server/Iverson.Api.Tests
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/Iverson.Sql/PostgresSchemaManager.cs Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Helpers/StartupNoOpFakes.cs Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs
git commit -m "detect schema type drift, throwing at registration and logging at startup"
```

---

### Task 3: Java array detection

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java:176,188,287`
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java`

Java currently **drops** array fields rather than mis-typing them: `detectClrType` has no branch for `List`/`Collection` and returns `null`, and `tryBuildPropertyDescriptor` skips the field on `null`. This task turns a dropped field into a registered one.

- [ ] **Step 1: Add a `Type`-taking overload that reports both element type and arrayness.**
`field.getType()` erases generics — `List<String>` arrives as `List`. Element recovery requires `getGenericType()`. Keep the existing `Class<?>` overload for the scalar path it already serves.

```java
    private record DetectedType(ClrType clrType, boolean isArray) {}

    private static DetectedType detectClrType(java.lang.reflect.Type type) {
        // byte[] is a primitive scalar — check before the array unwrap.
        if (type == byte[].class) return new DetectedType(ClrType.CLR_BYTES, false);

        if (type instanceof Class<?> c && c.isArray()) {
            ClrType element = detectClrType(c.getComponentType());
            return element == null ? null : new DetectedType(element, true);
        }
        if (type instanceof java.lang.reflect.ParameterizedType p
                && p.getRawType() instanceof Class<?> raw
                && java.util.Collection.class.isAssignableFrom(raw)) {
            java.lang.reflect.Type[] args = p.getActualTypeArguments();
            if (args.length == 1 && args[0] instanceof Class<?> elementClass) {
                ClrType element = detectClrType(elementClass);
                return element == null ? null : new DetectedType(element, true);
            }
            return null;
        }
        if (type instanceof Class<?> c) {
            ClrType scalar = detectClrType(c);
            return scalar == null ? null : new DetectedType(scalar, false);
        }
        return null;
    }
```

- [ ] **Step 2: Route both call sites through it.**
`buildKeyDescriptor` (`:176`) and `tryBuildPropertyDescriptor` (`:188`) pass `field.getGenericType()`. Detection returns `null` for unsupported types, so both sites must guard it — `buildKeyDescriptor` preserves today's `CLR_STRING` fallback rather than dereferencing:

```java
DetectedType detected = detectClrType(field.getGenericType());
ClrType clrType = detected != null ? detected.clrType() : ClrType.CLR_STRING;
boolean isArray = detected != null && detected.isArray();
```

`tryBuildPropertyDescriptor` still returns `null` when detection yields `null`, preserving the skip for genuine nav properties and custom types. A key field is never an array in practice; `buildKeyDescriptor` sets `isArray` from the detection result rather than special-casing.

- [ ] **Step 3: Add tests.** An entity with a `List<String>` and a `String[]` registers with `is_array` set and element `clr_type` `CLR_STRING`; a `byte[]` field still registers as the `ClrBytes` scalar with `is_array` false.

- [ ] **Step 4: Run the tests.**
```bash
mvn -f Iverson.Clients/Java/pom.xml test
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/SchemaRegistrar.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/SchemaRegistrarTest.java
git commit -m "detect array properties in the Java client"
```

---

### Task 4: Python array detection

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/core.py:54,126-130,162-173`
- Test: `Iverson.Clients/Python/tests/test_schema_registrar.py`

- [ ] **Step 1: Resolve annotations before inspecting them.**
First extend the existing import: `from typing import Generic, List, Optional, TypeVar, get_args, get_origin, get_type_hints`. `core.py` binds only selected `typing` names today, so a bare `typing.get_type_hints` reference is unbound.

`_build_request` walks raw `__annotations__` across the MRO, which holds **strings** under `from __future__ import annotations` — the style every existing Python entity uses. `get_origin('list[str]')` returns `None`. Replace the MRO walk with `get_type_hints(cls)`, which resolves the strings and merges the MRO itself.

The existing scalar path survives raw strings only by accident: `_python_type_to_clr` accepts `str | type` and `_PY_TO_CLR` keys on bare names, so `'str'` hits the `"str"` key while `'list[str]'` matches nothing.

- [ ] **Step 2: Return arrayness alongside the element type.**

```python
def _python_type_to_clr(type_hint: str | type | None) -> tuple[int, bool]:
    """Map a Python type annotation to (ClrType enum value, is_array)."""
    if type_hint is None:
        return mapping_pb.CLR_STRING, False
    # bytes is a scalar — check before the array unwrap.
    if type_hint is bytes:
        return mapping_pb.CLR_BYTES, False
    if get_origin(type_hint) in (list, set, tuple):
        args = get_args(type_hint)
        element = args[0] if args else None
        if element is bytes:
            return mapping_pb.CLR_BYTES, True
        name = getattr(element, "__name__", str(element)) if element is not None else ""
        return _PY_TO_CLR.get(name, mapping_pb.CLR_STRING), True
    name = type_hint if isinstance(type_hint, str) else getattr(type_hint, "__name__", str(type_hint))
    return _PY_TO_CLR.get(name, mapping_pb.CLR_STRING), False
```

- [ ] **Step 3: Consume both values.** At `core.py:163`, `clr_type, is_array = _python_type_to_clr(type_hint)`; the hardcoded `is_array=False` at `:173` becomes `is_array=is_array`.

- [ ] **Step 4: Add tests.** An entity with `tags: list[str]` and `counts: list[int]` registers with `is_array` set and element types `CLR_STRING`/`CLR_INT32`; a `blob: bytes` field still registers as the `ClrBytes` scalar with `is_array` false.

- [ ] **Step 5: Run the tests.**
```bash
cd Iverson.Clients/Python && pytest
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/tests/test_schema_registrar.py
git commit -m "detect array properties in the Python client"
```

---

### Task 5: Go array detection

**Files:**
- Modify: `Iverson.Clients/Go/iverson/registrar.go:69,82-95,174`
- Test: `Iverson.Clients/Go/iverson_test/registrar_test.go`

`goTypeToClr` returns a single value and the descriptor literal has no `IsArray` field, so a branch alone would be a no-op — the existing `reflect.Slice` arm already returns `CLR_STRING` for every non-`[]byte` slice.

- [ ] **Step 1: Return arrayness from `goTypeToClr`.**

```go
// goTypeToClr maps a reflect.Type to a ClrType proto enum value and whether it is an array.
func goTypeToClr(t reflect.Type) (pb.ClrType, bool) {
	if t.Kind() == reflect.Ptr {
		t = t.Elem()
	}
	switch t.Kind() {
	case reflect.String:
		return pb.ClrType_CLR_STRING, false
	case reflect.Int32:
		return pb.ClrType_CLR_INT32, false
	case reflect.Int, reflect.Int64:
		return pb.ClrType_CLR_INT64, false
	case reflect.Float32:
		return pb.ClrType_CLR_FLOAT, false
	case reflect.Float64:
		return pb.ClrType_CLR_DOUBLE, false
	case reflect.Bool:
		return pb.ClrType_CLR_BOOL, false
	case reflect.Slice:
		// []byte is a primitive scalar — check before the array unwrap.
		if t.Elem().Kind() == reflect.Uint8 {
			return pb.ClrType_CLR_BYTES, false
		}
		element, _ := goTypeToClr(t.Elem())
		return element, true
	case reflect.Struct:
		if t.PkgPath() == "time" && t.Name() == "Time" {
			return pb.ClrType_CLR_DATETIME, false
		}
		return pb.ClrType_CLR_STRING, false
	default:
		return pb.ClrType_CLR_STRING, false
	}
}
```

- [ ] **Step 2: Consume both values.** At `:69`, `clrType, isArray := goTypeToClr(sf.Type)`; add `IsArray: isArray,` to the `PropertyDescriptor` literal.

- [ ] **Step 3: Add tests.** A struct with `Tags []string` and `Counts []int` registers with `IsArray` true and element types `CLR_STRING`/`CLR_INT64`; a `Blob []byte` field still registers as `CLR_BYTES` with `IsArray` false.

- [ ] **Step 4: Run the tests.**
```bash
cd Iverson.Clients/Go && go test ./...
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/Go/iverson/registrar.go Iverson.Clients/Go/iverson_test/registrar_test.go
git commit -m "detect array properties in the Go client"
```

---

### Task 6: TypeScript `@IversonArray`

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/annotations.ts`
- Modify: `Iverson.Clients/TypeScript/src/core.ts:235,249`
- Modify: `Iverson.Clients/TypeScript/src/index.ts`
- Test: `Iverson.Clients/TypeScript/tests/schema-registrar.test.ts`

TypeScript cannot infer the element type: `Reflect.getMetadata('design:type', …)` returns the `Array` constructor, `emitDecoratorMetadata` erases the element, and an initialized `[]` carries no element either. So it is declared explicitly.

- [ ] **Step 1: Add the decorator and its accessor.**
`annotations.ts` first gains `import { ClrType } from '../generated/object_mapping.js';` alongside its `reflect-metadata` import — the same specifier `core.ts` uses. Without it the decorator's signature fails to compile with TS2304. No cycle results: `annotations.ts` imports nothing from `core.ts`.

Same `Reflect.defineMetadata` shape as the eight existing property decorators.

```ts
// ── @IversonArray(elementType) ─────────────────────────────────────────────────

const IVERSON_ARRAY_KEY = Symbol('iverson:array');

export function IversonArray(elementType: ClrType): PropertyDecorator {
    return (target, propertyKey) => {
        const existing: Map<string, ClrType> =
            Reflect.getMetadata(IVERSON_ARRAY_KEY, target.constructor) ?? new Map();
        existing.set(String(propertyKey), elementType);
        Reflect.defineMetadata(IVERSON_ARRAY_KEY, existing, target.constructor);
    };
}

export function getArrayFields(target: Function): Map<string, ClrType> {
    return Reflect.getMetadata(IVERSON_ARRAY_KEY, target) ?? new Map();
}
```

Declared on an entity as:

```ts
@IversonArray(ClrType.CLR_STRING)
tags: string[] = [];
```

- [ ] **Step 2: Read it in the registrar.**
In `core.ts`, add `getArrayFields` to the existing named-import block from `./annotations.js` (`:44-62`), then build `const arrayFields = getArrayFields(cls);` alongside the other accessor calls and replace the `clrType`/`isArray` derivation at `:235`/`:249`:

```ts
const designType = Reflect.getMetadata('design:type', proto, fieldName) as Function | undefined;
const arrayElement = arrayFields.get(fieldName);
if (designType === Array && arrayElement === undefined) {
    throw new Error(
        `${typeName}.${fieldName} is an array property but has no @IversonArray(elementType) ` +
        'decorator; TypeScript erases the element type, so it cannot be inferred. ' +
        'Add @IversonArray(ClrType.CLR_…) naming the element type.',
    );
}
const isArray = arrayElement !== undefined;
const clrType = arrayElement ?? (designType ? jsTypeToClr(designType.name) : ClrType.CLR_STRING);
```

`isArray` then replaces the hardcoded `false` in the descriptor literal. A property whose `design:type` is `Array` **without** the decorator is a registration error, not a silent `CLR_STRING` — leaving it to the existing fallback would reproduce the silent-wrong-declaration class this work exists to remove.

- [ ] **Step 3: Export both symbols.** `index.ts` adds `IversonArray` and `getArrayFields` to the `annotations.js` export block, and a new `export { ClrType }` for the generated proto enum — without it a consumer cannot name the decorator's argument, since `index.ts` exposes no generated type today.

- [ ] **Step 4: Add tests.** An entity with `@IversonArray(ClrType.CLR_STRING) tags: string[]` registers with `isArray` true and `clrType` `CLR_STRING`; an `Array`-typed property **without** the decorator fails registration; a non-array property is unaffected.

- [ ] **Step 5: Run the tests.**
```bash
cd Iverson.Clients/TypeScript && npm test && npm run build
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/TypeScript/src/annotations.ts Iverson.Clients/TypeScript/src/core.ts Iverson.Clients/TypeScript/src/index.ts Iverson.Clients/TypeScript/tests/schema-registrar.test.ts
git commit -m "add IversonArray decorator to the TypeScript client"
```

---

### Task 7: End-to-end array round-trip

**Files:**
- Test: `Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs`

**Interfaces — Consumes:** Tasks 1 and 2.

This is the test that would have caught the original bug, and the empirical check on the Postgres semantics the spec's §3 reasons about.

- [ ] **Step 1: Round-trip an entity with array properties.**
Apply a schema with a `TEXT[]` and an `INTEGER[]` column via `PostgresSchemaManager`, write through the `json_populate_record` path, read back through `row_to_json`, and assert the elements survive as JSON arrays — not as a JSON string. Include the negative-control shape in an assertion comment so a future regression to `TEXT` is legible.

- [ ] **Step 2: Cover both DDL paths.**
An array property on a newly registered type (`CREATE TABLE`, which emits no default) and an array property added to an already-registered type (`ALTER TABLE ADD COLUMN`, which is the only path exercising `GetDefaultForType`). A test against a fresh database takes only the first, which is why the second is stated explicitly.

- [ ] **Step 3: Run the tests.**
```bash
dotnet test Iverson.Server/Iverson.Sql.Tests
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Sql.Tests/PostgresIntegrationTests.cs
git commit -m "add end-to-end array round-trip and DDL-path tests"
```

## Tasks NOT in this plan

Inherited from the spec's "Out of scope":

Converting existing mis-typed columns — no `ALTER … USING`; the operator migrates by hand after the drift error names the column. StarRocks `ARRAY<…>` representation and StarRocks drift detection. Correcting `float[]`'s `Keyword` payload kind. Java's silent `CLR_STRING` fallback (`SchemaRegistrar.java:177`) when type detection fails — real, same class, but its own change.

## Known issues inherited from spec

`ClrTypeToEngagementType` (`SchemaBuilder.cs:278-279`) defaults to `"STRING"` for any SQL type absent from `SqlTypeMap`. With the table total over `ClrType` this default becomes unreachable for declared types, but it remains as a silent fallback rather than an error.
