# Pipeline Aggregations: Design Proposal

**Date:** 2026-06-27  
**Status:** Superseded by `docs/superpowers/specs/2026-07-05-pipeline-cte-dsl-design.md` (implemented)

---

## Problem

The current `AggregateAsync` API runs each `AggregationSpec` as an independent SQL query executed in parallel via `Task.WhenAll`. This covers flat metrics (AVG, SUM, COUNT) and flat buckets (Terms, DateHistogram, Range), but cannot express computations where one result feeds into the next:

- Top-N rows per group ("top 5 articles per author by recency")
- Filter-then-aggregate ("for articles with >1000 views, avg read time per author")
- Derived ratios ("articles this month as % of all articles")
- Ranked buckets ("rank authors by article count, return top 10")
- Running totals and cumulative sums over a date axis
- Bucket-then-metric-on-bucket ("date histogram, then avg read time per date bucket")

All of these are expressible as a chain of CTEs in StarRocks SQL. The proposal adds a `Pipeline<T>` builder that compiles to a single CTE-chain query.

---

## Design Overview

A pipeline is a **linear sequence of named steps**. Each step takes the output of the previous step as its input CTE. The final step determines the response shape.

```
base CTE → [window step] → [filter step] → [aggregate step] → [having step] → response
```

The server compiles the entire step chain into one SQL statement and executes it as a single round-trip.

---

## Client API

### Entry Point

```csharp
// New static entry — parallel to Query.For<T>()
var pipeline = Pipeline.For<Article>();
```

`Pipeline.For<T>()` returns a `PipelineBuilder<T>`, which is a separate type from `QueryBuilder<T>`.

### Step Methods

#### Filter

Applies `WHERE` clauses to the current CTE. Uses the same property-expression syntax as `QueryBuilder<T>`.

```csharp
Pipeline.For<Article>()
    .Filter(a => a.IsPublished, EqualTo, true)
    .Filter(a => a.PublishedAt, GreaterThan, DateTime.UtcNow.AddDays(-90))
```

Multiple `.Filter` calls on the same step are AND-combined. An explicit `.Or()` / `.Not()` overload handles other clause types:

```csharp
    .Filter(a => a.AuthorId, In, new[] { id1, id2 })
    .FilterNot(a => a.IsDraft, EqualTo, true)
    .FilterOr(a => a.TagCount, GreaterThan, 0)
```

#### Window

Adds one or more computed columns via window functions. Each call introduces a new named column in a new CTE.

```csharp
// ROW_NUMBER() OVER (PARTITION BY AuthorId ORDER BY PublishedAt DESC)
.Window(w => w
    .RowNumber("rn", partitionBy: a => a.AuthorId,
                     orderBy:    a => a.PublishedAt, descending: true))

// Multiple window columns in one step
.Window(w => w
    .RowNumber("rn_by_author",   partitionBy: a => a.AuthorId,   orderBy: a => a.PublishedAt, descending: true)
    .Rank(     "rank_by_views",  partitionBy: a => a.AuthorId,   orderBy: a => a.ViewCount,   descending: true)
    .RunningSum("cumulative_views", a => a.ViewCount, orderBy: a => a.PublishedAt))
```

Supported window functions:

| Method | SQL | Description |
|--------|-----|-------------|
| `RowNumber(alias, partitionBy, orderBy)` | `ROW_NUMBER() OVER (...)` | 1-based rank, no ties |
| `Rank(alias, partitionBy, orderBy)` | `RANK() OVER (...)` | Rank with gaps on ties |
| `DenseRank(alias, partitionBy, orderBy)` | `DENSE_RANK() OVER (...)` | Rank without gaps |
| `RunningSum(alias, field, orderBy)` | `SUM(field) OVER (ORDER BY ...)` | Cumulative sum |
| `RunningAvg(alias, field, orderBy)` | `AVG(field) OVER (ORDER BY ...)` | Cumulative average |
| `Lag(alias, field, offset)` | `LAG(field, offset) OVER (...)` | Prior row value |
| `Lead(alias, field, offset)` | `LEAD(field, offset) OVER (...)` | Next row value |

#### FilterOn

Filters using a computed column introduced by a prior `Window` step, or any aggregated alias introduced by `Aggregate`.

```csharp
// Keep only the top 3 rows per author partition
.Window(w => w.RowNumber("rn", partitionBy: a => a.AuthorId, orderBy: a => a.PublishedAt, descending: true))
.FilterOn("rn", LessThanOrEquals, 3)
```

`FilterOn` takes a string alias (not a property expression) because it references a computed column, not a schema field.

#### Aggregate

Groups the current CTE and computes metrics. Multiple `.GroupBy` calls produce a composite key.

```csharp
.Aggregate(agg => agg
    .GroupBy(a => a.AuthorId)
    .GroupBy(a => a.PublishedAt, DateTrunc.Month)   // optional truncation
    .Count("article_count")
    .Avg(a => a.ReadTime, "avg_read_time")
    .Sum(a => a.ViewCount, "total_views")
    .Max(a => a.PublishedAt, "latest_at"))
```

Supported aggregate functions:

| Method | SQL |
|--------|-----|
| `Count(alias)` | `COUNT(*) AS alias` |
| `CountDistinct(field, alias)` | `COUNT(DISTINCT field) AS alias` |
| `Avg(field, alias)` | `AVG(field) AS alias` |
| `Sum(field, alias)` | `SUM(field) AS alias` |
| `Min(field, alias)` | `MIN(field) AS alias` |
| `Max(field, alias)` | `MAX(field) AS alias` |

`GroupBy` with `DateTrunc` maps to StarRocks `DATE_TRUNC('month', field)`.

#### Having

Filters on the output of an `Aggregate` step. Takes string aliases.

```csharp
.Having("article_count", GreaterThan, 5)
.Having("avg_read_time", LessThan, 600.0)
```

#### Sort and Limit

```csharp
.Sort(a => a.PublishedAt, descending: true)   // field name from schema
.SortOn("article_count", descending: true)    // computed alias
.Limit(20)
```

---

## Full Examples

### Top 5 articles per author (past 90 days)

```csharp
var pipeline = Pipeline.For<Article>()
    .Filter(a => a.IsPublished, EqualTo, true)
    .Filter(a => a.PublishedAt, GreaterThan, DateTime.UtcNow.AddDays(-90))
    .Window(w => w.RowNumber("rn",
        partitionBy: a => a.AuthorId,
        orderBy:     a => a.PublishedAt, descending: true))
    .FilterOn("rn", LessThanOrEquals, 5)
    .Sort(a => a.AuthorId)
    .SortOn("rn");

await foreach (var row in coordinator.PipelineAsync(pipeline))
    Console.WriteLine($"{row["AuthorId"]} #{row["rn"]}: {row["Title"]}");
```

Generated SQL:
```sql
WITH base AS (
    SELECT * FROM `Article`
    WHERE `IsPublished` = @p0
      AND `PublishedAt` > @p1
),
windowed AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY `AuthorId` ORDER BY `PublishedAt` DESC) AS rn
    FROM base
),
filtered AS (
    SELECT * FROM windowed WHERE rn <= @p2
)
SELECT * FROM filtered
ORDER BY `AuthorId` ASC, rn ASC
```

---

### Authors with >5 published articles, ranked by article count

```csharp
var pipeline = Pipeline.For<Article>()
    .Filter(a => a.IsPublished, EqualTo, true)
    .Aggregate(agg => agg
        .GroupBy(a => a.AuthorId)
        .Count("article_count")
        .Avg(a => a.ReadTime, "avg_read_time"))
    .Having("article_count", GreaterThan, 5)
    .SortOn("article_count", descending: true)
    .Limit(10);
```

Generated SQL:
```sql
WITH base AS (
    SELECT * FROM `Article` WHERE `IsPublished` = @p0
),
agg AS (
    SELECT `AuthorId`,
           COUNT(*) AS article_count,
           AVG(`ReadTime`) AS avg_read_time
    FROM base
    GROUP BY `AuthorId`
    HAVING article_count > @p1
)
SELECT * FROM agg
ORDER BY article_count DESC
LIMIT 10
```

---

### Monthly article volume with cumulative total

```csharp
var pipeline = Pipeline.For<Article>()
    .Filter(a => a.IsPublished, EqualTo, true)
    .Aggregate(agg => agg
        .GroupBy(a => a.PublishedAt, DateTrunc.Month)
        .Count("article_count"))
    .Window(w => w.RunningSum("cumulative_count", "article_count",
        orderBy: "PublishedAt_month"))
    .SortOn("PublishedAt_month");
```

Generated SQL:
```sql
WITH base AS (
    SELECT * FROM `Article` WHERE `IsPublished` = @p0
),
agg AS (
    SELECT DATE_TRUNC('month', `PublishedAt`) AS PublishedAt_month,
           COUNT(*) AS article_count
    FROM base
    GROUP BY PublishedAt_month
),
windowed AS (
    SELECT *,
           SUM(article_count) OVER (ORDER BY PublishedAt_month ASC) AS cumulative_count
    FROM agg
)
SELECT * FROM windowed
ORDER BY PublishedAt_month ASC
```

---

### Derived ratio: this month's articles as % of 90-day total

```csharp
var pipeline = Pipeline.For<Article>()
    .Filter(a => a.IsPublished, EqualTo, true)
    .Filter(a => a.PublishedAt, GreaterThan, DateTime.UtcNow.AddDays(-90))
    .Aggregate(agg => agg
        .GroupBy(a => a.PublishedAt, DateTrunc.Month)
        .Count("article_count"))
    .Derive("pct_of_total",
        expr: "100.0 * article_count / SUM(article_count) OVER ()")
    .SortOn("PublishedAt_month", descending: true);
```

`Derive` adds a free-form computed column in a new CTE step. The expression is validated (no subqueries; only references to aliases from the prior step) but otherwise passed through as SQL.

Generated SQL:
```sql
WITH base AS (...),
agg AS (...),
derived AS (
    SELECT *,
           100.0 * article_count / SUM(article_count) OVER () AS pct_of_total
    FROM agg
)
SELECT * FROM derived ORDER BY PublishedAt_month DESC
```

---

## Response Shape

`PipelineAsync` returns `IAsyncEnumerable<IReadOnlyDictionary<string, object?>>` — rows as string-keyed dictionaries.

The column set depends on the final step in the pipeline:
- After a `Filter` or `Window` step: all schema columns plus any computed aliases
- After an `Aggregate` step: the GROUP BY key columns plus the metric aliases
- After a `Derive` step: whatever the prior step produced plus the derived alias

This is looser than the typed `SearchResult<T>` returned by `SearchAsync`. The trade-off is intentional: pipeline outputs are analytical and often don't map cleanly to the entity type.

A typed variant can be layered on top:

```csharp
// Typed projection (optional)
await foreach (var row in coordinator.PipelineAsync<AuthorStats>(pipeline))
    Console.WriteLine($"{row.AuthorId}: {row.ArticleCount} articles");
```

Where `AuthorStats` is any C# type with properties matching the output alias names. Deserialization follows the same `StructConverter.FromStruct<T>` path used by `GetAsync`.

---

## Proto Changes

A new RPC and message set in `object_search.proto`:

```protobuf
service ObjectSearchService {
    // ... existing RPCs ...
    rpc Pipeline (PipelineRequest) returns (stream PipelineResponse);
}

message PipelineRequest {
    string              type_name = 1;
    repeated PipelineStep steps   = 2;
    int32               limit     = 3;  // LIMIT on the final step (0 = no limit)
    string              trace_id  = 4;
}

message PipelineResponse {
    google.protobuf.Struct row      = 1;
    string                 trace_id = 2;
}

message PipelineStep {
    oneof kind {
        PipelineFilterStep    filter    = 1;
        PipelineWindowStep    window    = 2;
        PipelineAggregateStep aggregate = 3;
        PipelineHavingStep    having    = 4;
        PipelineSortStep      sort      = 5;
        PipelineDeriveStep    derive    = 6;
    }
}

message PipelineFilterStep {
    repeated SearchClause clauses = 1;  // reuses existing SearchClause type
    SearchLogic           logic   = 2;
}

message PipelineWindowStep {
    repeated WindowFunction functions = 1;
}

message WindowFunction {
    string              alias        = 1;
    WindowFunctionKind  kind         = 2;
    string              field        = 3;   // source column/alias (empty for COUNT)
    string              partition_by = 4;   // field name or alias
    string              order_by     = 5;   // field name or alias
    bool                descending   = 6;
    int32               offset       = 7;   // LAG/LEAD offset
}

enum WindowFunctionKind {
    ROW_NUMBER   = 0;
    RANK         = 1;
    DENSE_RANK   = 2;
    RUNNING_SUM  = 3;
    RUNNING_AVG  = 4;
    LAG          = 5;
    LEAD         = 6;
}

message PipelineAggregateStep {
    repeated AggregateGroupBy group_by = 1;
    repeated AggregateMetric  metrics  = 2;
}

message AggregateGroupBy {
    string    field     = 1;
    DateTrunc date_trunc = 2;  // optional; NONE = no truncation
}

message AggregateMetric {
    string          alias = 1;
    AggregationType type  = 2;  // reuses existing AggregationType enum
    string          field = 3;  // empty for Count
}

enum DateTrunc {
    NONE    = 0;
    MINUTE  = 1;
    HOUR    = 2;
    DAY     = 3;
    WEEK    = 4;
    MONTH   = 5;
    QUARTER = 6;
    YEAR    = 7;
}

message PipelineHavingStep {
    repeated PipelineCondition conditions = 1;
}

message PipelineCondition {
    string         alias    = 1;
    SearchOperator operator = 2;
    SearchValue    value    = 3;
}

message PipelineSortStep {
    repeated PipelineSortField fields = 1;
}

message PipelineSortField {
    string alias      = 1;  // schema field name OR computed alias
    bool   descending = 2;
    bool   is_schema_field = 3;  // true = resolve via SchemaDescriptor; false = emit as-is
}

message PipelineDeriveStep {
    string alias = 1;
    string expr  = 2;  // SQL expression referencing prior-step aliases
}
```

---

## Server-Side SQL Generation

`StarRocksQueryBuilder` gains a new static method:

```csharp
internal static (string Sql, DynamicParameters Param) BuildPipeline(
    string tableName,
    SchemaDescriptor schema,
    IReadOnlyList<PipelineStep> steps,
    int limit)
```

The method maintains a `cteName` cursor (starting at `"base"`) and a `nextIdx` counter for parameter names. For each step it:

1. Emits one CTE block: `WITH {cteName} AS ({stepSql})`
2. Advances `cteName` to the next auto-generated name (`step1`, `step2`, etc.) or a named step alias
3. Validates that aliases referenced in `FilterOn`, `Having`, `Derive`, and `Sort` steps were produced by a prior step (prevents forward references and typos at query-build time, before hitting the database)

The final `SELECT * FROM {lastCteName}` + optional `LIMIT` closes the query.

---

## Validation Rules

These are enforced in `StarRocksQueryBuilder.BuildPipeline` before SQL emission, so errors surface as gRPC `InvalidArgument` rather than SQL parse errors:

1. A `FilterOn` / `Having` / `SortOn` / `Derive` expression must reference an alias defined in a prior step.
2. A `Window` step cannot follow an `Aggregate` step unless the window function references only aggregated aliases (not original schema fields that were dropped by the GROUP BY).
3. A `Derive` expression is restricted to alphanumerics, underscores, arithmetic operators, parentheses, and a whitelist of SQL functions (`SUM OVER`, `AVG OVER`). No subqueries, no semicolons.
4. `Limit` is only valid on the final step; earlier limits must be expressed as `FilterOn("rn", ...)` after a `RowNumber` window.

---

## `EntityCoordinator<T>` Addition

```csharp
// Untyped rows
public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> PipelineAsync(
    PipelineBuilder<T> pipeline,
    CancellationToken ct = default)

// Typed projection
public async IAsyncEnumerable<TResult> PipelineAsync<TResult>(
    PipelineBuilder<T> pipeline,
    CancellationToken ct = default)
    where TResult : class, new()
```

Both send a `PipelineRequest` to `ObjectSearchGrpcService.Pipeline`, which streams back `PipelineResponse` rows.

---

## Scope and Exclusions

**In scope:**
- `Filter`, `Window`, `Aggregate`, `Having`, `Sort`, `Limit`, `Derive` steps
- All window function kinds in the table above
- Composite `GroupBy` keys
- `DateTrunc` on GroupBy fields
- Typed and untyped response projection

**Out of scope for this iteration:**
- Pipeline branching (multiple inputs to one step, UNION, JOIN between pipeline outputs)
- Cross-entity pipelines (joining `Article` and `Author` tables in one pipeline)
- Parameterized pipeline templates (stored pipelines with substitutable parameters)
- Sub-pipelines (a `Filter` step whose input is itself a pipeline)
- `Derive` with arbitrary SQL subquery expressions — restricted to scalar expressions for safety

---

## Relationship to Existing Aggregation API

The existing `AggregateAsync(QueryBuilder<T>)` path remains unchanged. Pipeline aggregations are additive, not a replacement. The existing API is the right choice when:

- Each metric is independent (the current parallel-execution model is an advantage)
- Results map to the existing `AggregationResult` / `AggregationBucket` types that callers already parse

Pipeline aggregations are the right choice when:
- Steps are sequential and each depends on the prior output
- Window functions are needed
- A single round-trip matters for latency (current API: N queries in parallel; pipeline: 1 query)
