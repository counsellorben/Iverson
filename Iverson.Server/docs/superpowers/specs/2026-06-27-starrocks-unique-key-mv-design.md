# StarRocks Unique Key + Sync Materialized View Design

**Date:** 2026-06-27
**Status:** Approved

## Problem

The medium search query (`WHERE Category = 'sports' AND WordCount > 500 ORDER BY PublishedAt DESC LIMIT 50`) runs at 754ms p50 against 400K rows — ~20× slower than the simple and complex profiles. Root cause: no sort key aligned to the filter+sort pattern forces StarRocks to scan ~80K sports-category rows and sort ~53K qualifying rows before returning the top 50.

Two structural fixes are needed:
1. Change the table model from `PRIMARY KEY` to `UNIQUE KEY` so StarRocks sync materialized views become available.
2. Create a narrow sync MV sorted by the query's filter/sort columns, eliminating the sort step.

Secondary: `BuildSearch` emits `SELECT *`, preventing the optimizer from using the narrow MV. A field projection mechanism fixes both the optimizer gap and an unrelated client ergonomics need (avoid transferring large fields like `Body` when not needed).

## Scope

Five changes, all within `Iverson.Server` and `Iverson.Client`:

1. Change StarRocks table model from `PRIMARY KEY` to `UNIQUE KEY`
2. Create a sync MV per entity at registration time
3. Add `[IversonSearchKey(order)]` and `[IversonLargeField]` attributes to control the MV; auto-exclude `[IversonEmbedding]`/`[IversonChunk]` fields from the MV
4. Change `BuildSearch` to emit an explicit column list
5. Add `fields` include-list to `SearchRequest`; add `reset-starrocks` command to LoadTest

## Attributes

Two new attributes in `Iverson.Client.Attributes`, consistent with `[IversonKey]`, `[IversonEmbedding]`, `[IversonChunk]`:

**`[IversonSearchKey(int order)]`** — marks a property as part of the MV sort key. Properties are sorted by `order` ascending when building the `ORDER BY` clause. An entity with no `[IversonSearchKey]` properties gets no MV.

**`[IversonLargeField]`** — explicitly marks a property as excluded from the MV column list. The base table still stores the column; the MV does not. This is the fallback for entities whose large fields carry no `[IversonEmbedding]` or `[IversonChunk]` annotation.

### MV exclusion rule (hybrid)

A column is excluded from the MV if **any** of the following is true:
- It carries `[IversonLargeField]`
- It carries `[IversonEmbedding]` — embedding-source fields are large text by definition; semantic search for them goes through Qdrant, not StarRocks
- It carries `[IversonChunk]` — same reasoning as `[IversonEmbedding]`

This means production entities whose large text fields are already annotated with `[IversonEmbedding]` or `[IversonChunk]` need no extra annotation for correct MV exclusion. `[IversonLargeField]` exists for large fields that are never vectorized.

**`BenchmarkArticle`** — `Body` is large but has no embeddings, so `[IversonLargeField]` is required:

```csharp
[IversonSearchKey(0)] public string          Category    { get; set; }
[IversonSearchKey(1)] public DateTimeOffset  PublishedAt { get; set; }
[IversonLargeField]   public string          Body        { get; set; }
```

**Typical production entity** — `Body` carries `[IversonEmbedding]`, so no additional annotation is needed:

```csharp
[IversonSearchKey(0)]  public string         Category    { get; set; }
[IversonSearchKey(1)]  public DateTimeOffset PublishedAt { get; set; }
[IversonEmbedding]     public string         Body        { get; set; }  // excluded from MV automatically
```

Both produce an MV sorted by `(Category, PublishedAt)` covering all columns except `Body`.

## Schema Layer

### `SchemaDescriptor`

Two new collections populated by `SchemaBuilder.Build()`:

- `SearchKeyColumns: IReadOnlyList<(string Name, int Order)>` — from `[IversonSearchKey]`, sorted by `Order`
- `LargeFieldColumns: IReadOnlySet<string>` — union of columns carrying `[IversonLargeField]`, `[IversonEmbedding]`, or `[IversonChunk]`

### `StarRocksTableSchema`

Two new fields consumed by `ApplyTableAsync`:

- `MvSortKey: IReadOnlyList<string>` — column names in sort key order; empty = no MV created
- `MvExcludedColumns: IReadOnlySet<string>` — column names omitted from the MV

`SchemaBuilder.ToStarRocksTableSchema()` derives both from the descriptor:
- `MvSortKey` = `SearchKeyColumns` sorted by `Order`, mapped to column names
- `MvExcludedColumns` = `LargeFieldColumns`

## Repository Layer

### `ApplyTableAsync` — DDL changes

**Table model** switches from `PRIMARY KEY` to `UNIQUE KEY`:

```sql
CREATE TABLE IF NOT EXISTS `{table}` (
    `{keyCol}` {keyType} NOT NULL,
    {cols...}
) ENGINE=OLAP
UNIQUE KEY(`{keyCol}`)
DISTRIBUTED BY HASH(`{keyCol}`) BUCKETS 4
PROPERTIES ("replication_num" = "1")
```

**MV creation** — after creating/verifying the table, if `schema.MvSortKey` is non-empty:

```sql
CREATE MATERIALIZED VIEW IF NOT EXISTS `{table}_search_mv`
AS SELECT `{col1}`, `{col2}`, ...    -- all columns except MvExcludedColumns
FROM `{table}`
ORDER BY `{sortKey1}`, `{sortKey2}`, ...
```

MV name is `{tableName}_search_mv` (one MV per table, predictable). `IF NOT EXISTS` makes the call idempotent — re-registering an existing schema is a no-op.

The existing `ALTER TABLE ADD COLUMN` path for schema evolution is unchanged. MV columns are **not** automatically updated when new columns are added — that requires a `reset-starrocks` + re-seed cycle.

### `IStarRocksRepository` — no new methods

`DropAllTablesAsync` is **not** added to the shared interface or the `StarRocksRepository` implementation. It belongs exclusively in the LoadTest (see below).

## Query Builder

### `BuildSearch` signature

```csharp
internal static (string Sql, DynamicParameters Param) BuildSearch(
    string tableName,
    SchemaDescriptor schema,
    SearchQuery? query,
    int page,
    int pageSize,
    IReadOnlyList<string>? fields = null)
```

### Column list behavior

- **`fields` is null or empty** → `SELECT \`{keyCol}\`, \`{col1}\`, \`{col2}\`, ...` — key column first, then all scalar columns from `SchemaDescriptor`. Identical to current `SELECT *` behavior but explicit. Optimizer uses base table.
- **`fields` is non-empty** → key column always included, then only the requested columns that resolve via `ResolveColumn` (OrdinalIgnoreCase). Unknown field names are silently ignored, consistent with how unknown filter clause properties are handled today. When the field list excludes all MV-excluded columns (those carrying `[IversonLargeField]`, `[IversonEmbedding]`, or `[IversonChunk]`), the StarRocks optimizer rewrites the query to use the MV automatically.

`BuildAggregate` is unchanged — aggregation queries use explicit column expressions already.

## Proto Changes

`SearchRequest` in `object_search.proto` gets one new field:

```protobuf
message SearchRequest {
    string      type_name = 1;
    SearchQuery query     = 2;
    int32       page      = 3;
    int32       page_size = 4;
    string      trace_id  = 5;
    repeated string fields = 6;  // property names to include; empty = all fields
}
```

`ObjectSearchGrpcService.Search` passes `request.Fields` to `BuildSearch`. No other service changes — `DictToProtoStruct` projects naturally from whatever columns the query returned.

## LoadTest: `reset-starrocks` Command

New command registered in `Program.cs` alongside `seed`, `read-path`, `write-path`:

```
dotnet run -- reset-starrocks
```

Implementation opens a `MySqlConnection` directly using the StarRocks connection string from `LoadTestConfig` (consistent with `ReadPathScenario` using `NpgsqlConnection` directly). Executes `SHOW TABLES`, then `DROP TABLE IF EXISTS \`{name}\`` for each — StarRocks cascades the drop to associated MVs automatically. Prints each dropped table name. No confirmation prompt; the command name is explicit.

Typical reset-and-reseed workflow:

```
dotnet run -- reset-starrocks   # drop all tables + MVs
# restart server                # RegisterSchema → ApplyTableAsync → Unique Key + MV
dotnet run -- seed              # repopulate
dotnet run -- read-path         # benchmark
```

## Expected Impact

With `BenchmarkArticle` annotated and a client that requests `fields: ["Id", "Category", "WordCount", "PublishedAt", "Title"]` (excluding `Body`):

- Medium query sorts 0 rows — the MV is pre-sorted by `(Category, PublishedAt)`; the optimizer scans ~75 rows (50 / 0.67 selectivity) and stops
- Expected p50: ~5–15ms vs. current 754ms
- Simple and complex profiles unaffected
- Write overhead: marginally higher per-upsert (Unique Key merge-on-write vs. PK delete-bitmap); negligible at 223 RPS

With no field projection (default), the optimizer uses the base table — identical performance to today for `SELECT *` queries. The MV benefit is opt-in via field projection.

## Migration Path

1. `dotnet run -- reset-starrocks` — drops all existing PK tables and their MVs
2. Server restart — `RegisterSchema` calls `ApplyTableAsync` → recreates as Unique Key + MV
3. `dotnet run -- seed` — repopulates 400K rows
4. `dotnet run -- read-path` — compaction runs during/after seed; first read-path run may see slightly elevated latency until compaction catches up on the MV
