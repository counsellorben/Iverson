# Pipeline CTE — Proto + Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `Pipeline` RPC that compiles a chain of named, SQL-shaped steps into a single StarRocks CTE-chain query, with full validation (column tracking, DAG `reads`, joins against prior CTEs or entity tables) surfacing as gRPC `InvalidArgument` before any SQL executes.

**Architecture:** One new proto message set + RPC on `ObjectSearchService` (streams existing `SearchResponse`). A new server-side compiler `StarRocksPipelineBuilder` runs two passes: pass 1 validates the request and computes every step's output column set; pass 2 emits `WITH name AS (...)` chain SQL. The gRPC method mirrors the existing `GroupBy` method exactly (readiness gate via repository, Dapper streaming, `DictToProtoStruct`).

**Tech Stack:** .NET (server `Iverson.Api`), proto3 (`object_search.proto`, compiled into `Iverson.Client.Contracts` via `Grpc.Tools`), Dapper `DynamicParameters`, xUnit + FluentAssertions + NSubstitute, Testcontainers (live StarRocks integration tests).

**Spec:** `docs/superpowers/specs/2026-07-05-pipeline-cte-dsl-design.md`

## Global Constraints

- Every validation failure must throw `RpcException` with `StatusCode.InvalidArgument` and a message naming the step and the offending reference — never surface a StarRocks SQL error for a malformed request.
- Windows XOR aggregation within a step (a step with any `windows` must have empty `group_by`, `metrics`, and `having`).
- A step with `joins` must have a non-empty `select`; a step with `group_by`/`metrics` must have an empty `select`.
- Parameter prefixes: base WHERE uses `s0_p{n}`; step *i* (1-based) uses `s{i}_p{n}` for WHERE and `s{i}_h{n}` for HAVING — all sharing one `DynamicParameters`.
- Default final limit 10,000 (when `limit == 0`), matching `GroupBy`.
- Step names and `reads` resolve case-insensitively; emitted SQL uses the canonical (as-declared) name.
- `MetricSpec.expression` remains raw trusted SQL, same posture as the existing `BuildMetricExpr` comment in `StarRocksQueryBuilder.cs` — do not add escaping.
- Run server tests with: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"` (integration tests need Docker and are run explicitly in Task 7).

---

### Task 1: Proto contract

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_search.proto` (service block at line ~9; append messages at end of file)

**Interfaces:**
- Produces: `PipelineRequest`, `PipelineStep`, `PipelineJoin`, `JoinCondition`, `SelectItem`, `GroupKey`, `DeriveColumn`, `WindowFunction`, `WindowFunctionKind`, `DateTrunc` C# types in `Iverson.Client.Contracts`, and `ObjectSearchService.ObjectSearchServiceBase.Pipeline` virtual method. All later tasks consume these exact names.

- [ ] **Step 1: Add the RPC to the service block**

In `Iverson.Clients/Common/Proto/object_search.proto`, change the service to:

```protobuf
service ObjectSearchService {
    rpc Search         (SearchRequest)        returns (stream SearchResponse);
    rpc SearchSimilar  (SearchSimilarRequest) returns (stream SearchResponse);
    rpc SearchChunks   (SearchChunksRequest)  returns (stream ChunkSearchResponse);
    rpc Aggregate      (AggregateRequest)     returns (AggregateResponse);
    rpc GroupBy        (GroupByRequest)       returns (stream SearchResponse);
    rpc Pipeline       (PipelineRequest)      returns (stream SearchResponse);
}
```

- [ ] **Step 2: Append the pipeline message set at the end of the file**

```protobuf
// ── Pipeline (CTE chains) ────────────────────────────────────────────────────
//
// Compiles a chain of named, SQL-shaped steps into ONE StarRocks query built
// from chained CTEs (WITH name AS (...)). Each step reads the previous step by
// default, or any earlier named step via `reads`, and may join prior steps'
// CTEs or registered entity tables. Rows stream back as SearchResponse with a
// constant score of 1.0, exactly like GroupBy.

message PipelineRequest {
    string                type_name  = 1;
    repeated SearchClause base_where = 2;   // WHERE on the implicit "base" step
    SearchLogic           base_logic = 3;
    repeated PipelineStep steps      = 4;
    repeated SearchSort   order_by   = 5;   // final ORDER BY (last step's columns/aliases)
    int32                 limit      = 6;   // 0 = default 10000
    string                trace_id   = 7;
}

message PipelineStep {
    string                  name        = 1;  // CTE name; required, unique, valid identifier
    string                  reads       = 2;  // input step name; empty = previous step
    repeated SearchClause   where       = 3;
    SearchLogic             where_logic = 4;
    repeated WindowFunction windows     = 5;  // XOR with group_by/metrics/having
    repeated GroupKey       group_by    = 6;
    repeated MetricSpec     metrics     = 7;  // reuses existing MetricSpec
    repeated SearchClause   having      = 8;  // property = metric alias from this step
    repeated DeriveColumn   derive      = 9;
    repeated PipelineJoin   joins       = 10;
    repeated SelectItem     select      = 11; // required when joins present; else optional
}

message PipelineJoin {
    string   source = 1;              // prior step name OR registered type name
    JoinKind kind   = 2;              // reuses existing enum
    repeated JoinCondition on = 3;    // equality pairs, AND-ed
}

message JoinCondition {
    string left  = 1;  // column in the step's input
    string right = 2;  // column in the join source
}

message SelectItem {
    string source = 1;  // step name or type name; empty = the step's input
    string column = 2;  // empty when all = true
    bool   all    = 3;  // emit source.* (expands the source's full column set)
    string alias  = 4;  // optional output alias for a single column
}

message GroupKey {
    string    field      = 1;
    DateTrunc date_trunc = 2;  // NONE = group by the raw column
}

message DeriveColumn {
    string alias = 1;
    string expr  = 2;  // validated scalar expression over input columns / step aliases
}

message WindowFunction {
    string             alias        = 1;
    WindowFunctionKind kind         = 2;
    string             field        = 3;  // source column (empty for ROW_NUMBER/RANK/DENSE_RANK)
    string             partition_by = 4;  // optional
    string             order_by     = 5;  // required for every kind
    bool               descending   = 6;
    int32              offset       = 7;  // LAG/LEAD only; 0 = default 1
}

enum WindowFunctionKind {
    ROW_NUMBER  = 0;
    RANK        = 1;
    DENSE_RANK  = 2;
    RUNNING_SUM = 3;
    RUNNING_AVG = 4;
    LAG         = 5;
    LEAD        = 6;
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
```

- [ ] **Step 3: Verify C# codegen and full solution build**

Run: `dotnet build Iverson.Server/Iverson.Api/Iverson.Api.csproj && dotnet build Iverson.Clients/DotNet/Iverson.Client.Contracts/Iverson.Client.Contracts.csproj`
Expected: both succeed; `Iverson.Client.Contracts` now exposes `PipelineRequest` etc. (the csproj's `<Protobuf Include="../../Common/Proto/*.proto" GrpcServices="Both" />` picks the change up automatically; the server references Contracts).

- [ ] **Step 4: Commit**

```bash
git add Iverson.Clients/Common/Proto/object_search.proto
git commit -m "feat(proto): Pipeline RPC and CTE step message set"
```

---

### Task 2: Parametrize BuildWhere/BuildHaving for reuse by the pipeline compiler

**Files:**
- Modify: `Iverson.Server/Iverson.Api/StarRocks/StarRocksQueryBuilder.cs:277-343` (`BuildWhere`) and `:353-408` (`BuildHaving`)
- Test: `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs` (append)

**Interfaces:**
- Produces (consumed by Tasks 4–5):
  - `internal static string BuildWhere(Func<string, string?> resolveQuoted, IEnumerable<SearchClause>? clauses, SearchLogic logic, DynamicParameters param, string paramPrefix, out int nextIdx)`
  - `internal static string BuildHaving(IEnumerable<SearchClause>? clauses, SearchLogic logic, DynamicParameters param, string paramPrefix)`
- The two existing `BuildWhere(SchemaDescriptor, ...)` / `BuildWhere(..., tableMap)` shapes and the existing `BuildHaving(clauses, logic, param)` signature keep working unchanged (they become wrappers), so `BuildSearch`/`BuildAggregate`/`BuildGroupBy` compile untouched.

- [ ] **Step 1: Write the failing tests**

Append to `StarRocksQueryBuilderTests.cs`:

```csharp
    // ── BuildWhere/BuildHaving — resolver + prefix overloads ──────────────────

    [Fact]
    public void BuildWhere_ResolverOverload_UsesPrefixAndResolver()
    {
        var param = new DynamicParameters();
        var clauses = new[]
        {
            new SearchClause
            {
                Property = "articles", Operator = SearchOperator.GreaterThan,
                Value = new SearchValue { NumberVal = 5 }, ClauseType = SearchClauseType.Filter
            }
        };

        var sql = StarRocksQueryBuilder.BuildWhere(
            p => p == "articles" ? "`articles`" : null,
            clauses, SearchLogic.And, param, "s2_p", out var next);

        sql.Should().Be("`articles` > @s2_p0");
        next.Should().Be(1);
        param.Get<double>("s2_p0").Should().Be(5);
    }

    [Fact]
    public void BuildWhere_ResolverOverload_SkipsUnresolvableColumns()
    {
        var param = new DynamicParameters();
        var clauses = new[]
        {
            new SearchClause
            {
                Property = "nope", Operator = SearchOperator.Equals,
                Value = new SearchValue { NumberVal = 1 }, ClauseType = SearchClauseType.Filter
            }
        };

        var sql = StarRocksQueryBuilder.BuildWhere(
            _ => null, clauses, SearchLogic.And, param, "s1_p", out _);

        sql.Should().BeEmpty();
    }

    [Fact]
    public void BuildHaving_PrefixOverload_UsesPrefix()
    {
        var param = new DynamicParameters();
        var clauses = new[]
        {
            new SearchClause
            {
                Property = "article_count", Operator = SearchOperator.GreaterThan,
                Value = new SearchValue { NumberVal = 3 }, ClauseType = SearchClauseType.Filter
            }
        };

        var sql = StarRocksQueryBuilder.BuildHaving(clauses, SearchLogic.And, param, "s3_h");

        sql.Should().Be("`article_count` > @s3_h0");
        param.Get<double>("s3_h0").Should().Be(3);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~BuildWhere_ResolverOverload|FullyQualifiedName~BuildHaving_PrefixOverload"`
Expected: FAIL — compile errors (no such overloads).

- [ ] **Step 3: Implement the overloads**

In `StarRocksQueryBuilder.cs`, replace the body of the existing `BuildWhere` with a thin wrapper and add the resolver core (keep the XML doc comment on the existing method):

```csharp
    internal static string BuildWhere(
        SchemaDescriptor schema,
        IEnumerable<SearchClause>? clauses,
        SearchLogic logic,
        DynamicParameters param,
        out int nextIdx,
        IReadOnlyDictionary<string, JoinContext>? tableMap = null)
    {
        Func<string, string?> resolve = tableMap is not null
            ? p => ResolveColumn(tableMap, p) is { } qc ? QuoteQualified(qc) : null
            : p => ResolveColumn(schema, p) is { } c ? $"`{c}`" : null;
        return BuildWhere(resolve, clauses, logic, param, "p", out nextIdx);
    }

    /// <summary>
    /// Core WHERE builder. <paramref name="resolveQuoted"/> maps a clause property to a
    /// fully-quoted, ready-to-embed SQL identifier (or null to skip the clause), and
    /// <paramref name="paramPrefix"/> names the Dapper parameters ("p" for plain queries;
    /// pipeline steps pass "s{i}_p" so multiple steps can share one DynamicParameters).
    /// </summary>
    internal static string BuildWhere(
        Func<string, string?> resolveQuoted,
        IEnumerable<SearchClause>? clauses,
        SearchLogic logic,
        DynamicParameters param,
        string paramPrefix,
        out int nextIdx)
    {
        nextIdx = 0;
        if (clauses is null) return "";

        var parts = new List<string>();

        foreach (var clause in clauses)
        {
            if (clause.Operator == SearchOperator.VectorSimilar) continue;

            var quotedCol = resolveQuoted(clause.Property);
            if (quotedCol is null) continue;

            var pName = $"{paramPrefix}{nextIdx++}";

            var condition = clause.Operator switch
            {
                SearchOperator.Equals => BuildEq(quotedCol, pName, clause.Value, param),
                SearchOperator.NotEquals =>
                    Condition($"{quotedCol} <> @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.Contains =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"%{clause.Value?.StringVal}%", param),
                SearchOperator.StartsWith =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"{clause.Value?.StringVal}%", param),
                SearchOperator.EndsWith =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"%{clause.Value?.StringVal}", param),
                SearchOperator.GreaterThan =>
                    Condition($"{quotedCol} > @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.GreaterThanOrEquals =>
                    Condition($"{quotedCol} >= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThan =>
                    Condition($"{quotedCol} < @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThanOrEquals =>
                    Condition($"{quotedCol} <= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.In => BuildIn(quotedCol, pName, clause.Value, param),
                _ => null
            };

            if (condition is null) continue;

            var wrapped = clause.ClauseType == SearchClauseType.MustNot
                ? $"NOT ({condition})"
                : condition;

            parts.Add(wrapped);
        }

        if (parts.Count == 0) return "";
        var sep = logic == SearchLogic.Or ? " OR " : " AND ";
        return string.Join(sep, parts);
    }
```

Note: when a `Condition(...)` branch returns null via `BuildIn` (empty IN list), the parameter index has already advanced — this matches today's behavior exactly; do not "fix" it.

For `BuildHaving`, change the signature to `internal static string BuildHaving(IEnumerable<SearchClause>? clauses, SearchLogic logic, DynamicParameters param, string paramPrefix = "h")` and replace the hardcoded `$"h{nextIdx++}"` with `$"{paramPrefix}{nextIdx++}"`. No other body changes. Also delete the now-duplicated operator-switch from the old `BuildWhere` body (the wrapper above replaces it entirely).

- [ ] **Step 4: Run the full non-integration test suite**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS — new tests green, all existing `StarRocksQueryBuilderTests`/`ObjectSearchGrpcServiceTests` still green (proving the wrappers preserved behavior).

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs
git commit -m "refactor(server): resolver+prefix overloads for BuildWhere/BuildHaving"
```

---

### Task 3: Pipeline pass 1 — validation and column tracking (no joins yet)

**Files:**
- Create: `Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs`
- Test: Create `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs`

**Interfaces:**
- Consumes: `StarRocksQueryBuilder.ResolveColumn(SchemaDescriptor, string)` (Task 2 file, unchanged), proto types from Task 1, `SchemaRegistry.Get(string)` returning `SchemaDescriptor?`.
- Produces (consumed by Tasks 4–5):
  - `internal static IReadOnlyList<StepColumns> TrackAndValidate(SchemaDescriptor schema, PipelineRequest request, SchemaRegistry registry)`
  - `internal sealed record StepColumns(string Name, Dictionary<string, string> Columns)` — `Columns` is OrdinalIgnoreCase, maps referenced-name → canonical output name. Index 0 is always the base step (`Name == "base"`).
  - Join handling is split across tasks: this task implements join-*source* resolution (`ResolveJoinSources`), the joins-require-select rule, and select-item output tracking (including items sourced from join sources). Task 5 adds ON-condition validation and all join SQL emission.

- [ ] **Step 1: Write the failing tests**

Create `StarRocksPipelineBuilderTests.cs`:

```csharp
using Dapper;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Api.StarRocks;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.StarRocks;

public class StarRocksPipelineBuilderTests
{
    private static SchemaDescriptor ArticleSchema() => new()
    {
        TypeName      = "Article",
        TableName     = "articles",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Title",       "text",        false),
            new ColumnDescriptor("Category",    "text",        true),
            new ColumnDescriptor("WordCount",   "integer",     true),
            new ColumnDescriptor("IsPublished", "boolean",     true),
            new ColumnDescriptor("PublishedAt", "timestamptz", true),
            new ColumnDescriptor("AuthorId",    "uuid",        true),
        ],
        FkColumns = [], VectorFields = [], ChunkFields = [], Relations = []
    };

    private static SchemaRegistry EmptyRegistry()
    {
        var sql = Substitute.For<IPostgresRepository>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        return new SchemaRegistry(sql, NullLogger<SchemaRegistry>.Instance);
    }

    private static PipelineRequest Request(params PipelineStep[] steps)
    {
        var r = new PipelineRequest { TypeName = "Article" };
        r.Steps.AddRange(steps);
        return r;
    }

    private static void AssertInvalid(Action act, string messagePart)
    {
        var ex = Assert.Throws<RpcException>(act);
        ex.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain(messagePart);
    }

    // ── Column tracking ────────────────────────────────────────────────────────

    [Fact]
    public void Track_BaseStep_ExposesSchemaColumns()
    {
        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(), EmptyRegistry());

        cols.Should().HaveCount(1);
        cols[0].Name.Should().Be("base");
        cols[0].Columns.Keys.Should().Contain(["Id", "Title", "WordCount"]);
    }

    [Fact]
    public void Track_WindowStep_AddsAliasToInputColumns()
    {
        var step = new PipelineStep { Name = "ranked" };
        step.Windows.Add(new WindowFunction
        {
            Alias = "rn", Kind = WindowFunctionKind.RowNumber,
            PartitionBy = "AuthorId", OrderBy = "PublishedAt", Descending = true
        });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry());

        cols[1].Name.Should().Be("ranked");
        cols[1].Columns.Keys.Should().Contain("rn");
        cols[1].Columns.Keys.Should().Contain("Title");   // input columns flow through
    }

    [Fact]
    public void Track_AggregateStep_ReplacesColumnsWithKeysAndAliases()
    {
        var step = new PipelineStep { Name = "by_author" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry());

        cols[1].Columns.Keys.Should().BeEquivalentTo(["AuthorId", "articles"]);
    }

    [Fact]
    public void Track_DateTruncKey_ProducesSuffixedAlias()
    {
        var step = new PipelineStep { Name = "monthly" };
        step.GroupBy.Add(new GroupKey { Field = "PublishedAt", DateTrunc = DateTrunc.Month });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry());

        cols[1].Columns.Keys.Should().Contain("PublishedAt_month");
    }

    [Fact]
    public void Track_ReadsSelectsEarlierStep()
    {
        var agg = new PipelineStep { Name = "agg" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var fromBase = new PipelineStep { Name = "raw", Reads = "base" };
        fromBase.Derive.Add(new DeriveColumn { Alias = "wc2", Expr = "WordCount * 2" });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(agg, fromBase), EmptyRegistry());

        // "raw" read base, not "agg" — so it still has Title plus its derive alias
        cols[2].Columns.Keys.Should().Contain(["Title", "wc2"]);
    }

    // ── Validation rules ──────────────────────────────────────────────────────

    [Fact]
    public void Validate_DuplicateStepName_Throws() =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(),
            Request(new PipelineStep { Name = "x" }, new PipelineStep { Name = "X" }),
            EmptyRegistry()), "x");

    [Fact]
    public void Validate_InvalidIdentifierStepName_Throws() =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(new PipelineStep { Name = "1bad name" }),
            EmptyRegistry()), "1bad name");

    [Fact]
    public void Validate_StepNamedBase_Throws() =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(new PipelineStep { Name = "base" }),
            EmptyRegistry()), "base");

    [Fact]
    public void Validate_ForwardReads_Throws()
    {
        var s1 = new PipelineStep { Name = "a", Reads = "b" };
        var s2 = new PipelineStep { Name = "b" };
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(s1, s2), EmptyRegistry()), "b");
    }

    [Fact]
    public void Validate_WindowsAndGroupByTogether_Throws()
    {
        var step = new PipelineStep { Name = "bad" };
        step.Windows.Add(new WindowFunction { Alias = "rn", Kind = WindowFunctionKind.RowNumber, OrderBy = "Id" });
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "bad");
    }

    [Fact]
    public void Validate_UnknownWhereColumn_Throws()
    {
        var step = new PipelineStep { Name = "f" };
        step.Where.Add(new SearchClause
        {
            Property = "Nope", Operator = SearchOperator.Equals,
            Value = new SearchValue { NumberVal = 1 }, ClauseType = SearchClauseType.Filter
        });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "Nope");
    }

    [Fact]
    public void Validate_HavingUnknownAlias_Throws()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });
        step.Having.Add(new SearchClause
        {
            Property = "misspelled", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 1 }, ClauseType = SearchClauseType.Filter
        });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "misspelled");
    }

    [Fact]
    public void Validate_DuplicateAliasWithinStep_Throws()
    {
        var step = new PipelineStep { Name = "w" };
        step.Windows.Add(new WindowFunction { Alias = "x", Kind = WindowFunctionKind.RowNumber, OrderBy = "Id" });
        step.Derive.Add(new DeriveColumn { Alias = "x", Expr = "WordCount + 1" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "x");
    }

    [Fact]
    public void Validate_WindowWithoutOrderBy_Throws()
    {
        var step = new PipelineStep { Name = "w" };
        step.Windows.Add(new WindowFunction { Alias = "rn", Kind = WindowFunctionKind.RowNumber });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "rn");
    }

    [Fact]
    public void Validate_RunningSumWithoutField_Throws()
    {
        var step = new PipelineStep { Name = "w" };
        step.Windows.Add(new WindowFunction
            { Alias = "cume", Kind = WindowFunctionKind.RunningSum, OrderBy = "PublishedAt" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "cume");
    }

    [Fact]
    public void Validate_DeriveWithUnknownIdentifier_Throws()
    {
        var step = new PipelineStep { Name = "d" };
        step.Derive.Add(new DeriveColumn { Alias = "r", Expr = "Bogus / WordCount" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "Bogus");
    }

    [Fact]
    public void Validate_DeriveWithSemicolon_Throws()
    {
        var step = new PipelineStep { Name = "d" };
        step.Derive.Add(new DeriveColumn { Alias = "r", Expr = "WordCount; DROP TABLE x" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "r");
    }

    [Fact]
    public void Validate_DeriveAllowsWhitelistedWindowExpr()
    {
        var agg = new PipelineStep { Name = "agg" };
        agg.GroupBy.Add(new GroupKey { Field = "Category" });
        agg.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var d = new PipelineStep { Name = "pct" };
        d.Derive.Add(new DeriveColumn { Alias = "pct_of_total", Expr = "100.0 * n / SUM(n) OVER ()" });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(agg, d), EmptyRegistry());

        cols[2].Columns.Keys.Should().Contain("pct_of_total");
    }

    [Fact]
    public void Validate_JoinsWithoutSelect_Throws()
    {
        var step = new PipelineStep { Name = "j" };
        step.Joins.Add(new PipelineJoin { Source = "Author", Kind = JoinKind.Inner });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "select");
    }

    [Fact]
    public void Validate_SelectOnAggregateStep_Throws()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });
        step.Select.Add(new SelectItem { All = true });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "agg");
    }
}
```

Note on proto property names in C#: `partition_by` → `PartitionBy`, `order_by` → `OrderBy`, `group_by` → `GroupBy`, `date_trunc` → `DateTrunc`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~StarRocksPipelineBuilderTests"`
Expected: FAIL — `StarRocksPipelineBuilder` does not exist.

- [ ] **Step 3: Implement pass 1**

Create `Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs`:

```csharp
using System.Text.RegularExpressions;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;

namespace Iverson.Api.StarRocks;

/// <summary>
/// One pipeline step's output column set: <see cref="Columns"/> maps a referenced
/// name (case-insensitive) to the canonical output column name emitted in SQL.
/// </summary>
internal sealed record StepColumns(string Name, Dictionary<string, string> Columns);

/// <summary>
/// Compiles a <see cref="PipelineRequest"/> into a single StarRocks CTE-chain query.
/// Pass 1 (<see cref="TrackAndValidate"/>) computes every step's output column set and
/// rejects invalid references as gRPC InvalidArgument before any SQL is built.
/// </summary>
internal static class StarRocksPipelineBuilder
{
    internal const string BaseStepName = "base";

    private static readonly Regex IdentifierRx = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex TokenRx      = new("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // Identifiers a Derive expression may use besides input columns. Anything else —
    // including SELECT/FROM/WHERE, which blocks subqueries — fails validation.
    private static readonly HashSet<string> DeriveWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "MIN", "MAX", "COUNT", "OVER", "PARTITION", "BY", "ORDER",
        "ASC", "DESC", "COALESCE", "NULLIF", "ROUND", "ABS", "AND", "OR", "NOT", "NULL"
    };

    internal static IReadOnlyList<StepColumns> TrackAndValidate(
        SchemaDescriptor schema,
        PipelineRequest request,
        SchemaRegistry registry)
    {
        var steps = new List<StepColumns>();

        var baseColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [schema.KeyColumn.Name] = schema.KeyColumn.Name
        };
        foreach (var c in schema.ScalarColumns)
            baseColumns[c.Name] = c.Name;
        steps.Add(new StepColumns(BaseStepName, baseColumns));

        foreach (var clause in request.BaseWhere)
            RequireColumn(BaseStepName, baseColumns, clause.Property);

        foreach (var step in request.Steps)
        {
            ValidateStepName(step, steps, registry);
            var input = ResolveInput(step, steps);
            var output = ValidateStepAndComputeOutput(step, input, steps, registry);
            steps.Add(new StepColumns(step.Name, output));
        }

        // Final ORDER BY resolves against the last step's output.
        var last = steps[^1];
        foreach (var sort in request.OrderBy)
            RequireColumn(last.Name, last.Columns, sort.Property);

        return steps;
    }

    private static void ValidateStepName(
        PipelineStep step, List<StepColumns> earlier, SchemaRegistry registry)
    {
        if (string.IsNullOrEmpty(step.Name) || !IdentifierRx.IsMatch(step.Name))
            throw Invalid($"Step name '{step.Name}' is not a valid identifier.");
        if (step.Name.Equals(BaseStepName, StringComparison.OrdinalIgnoreCase))
            throw Invalid($"Step name '{step.Name}' is reserved for the implicit base step.");
        if (earlier.Any(s => s.Name.Equals(step.Name, StringComparison.OrdinalIgnoreCase)))
            throw Invalid($"Duplicate step name '{step.Name}'.");
        if (registry.Get(step.Name) is not null)
            throw Invalid($"Step name '{step.Name}' collides with a registered type name.");
    }

    private static StepColumns ResolveInput(PipelineStep step, List<StepColumns> earlier)
    {
        if (string.IsNullOrEmpty(step.Reads)) return earlier[^1];

        return earlier.FirstOrDefault(
                s => s.Name.Equals(step.Reads, StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid(
                $"Step '{step.Name}': reads '{step.Reads}' does not name an earlier step.");
    }

    private static Dictionary<string, string> ValidateStepAndComputeOutput(
        PipelineStep step,
        StepColumns input,
        List<StepColumns> earlier,
        SchemaRegistry registry)
    {
        var isAggregate = step.GroupBy.Count > 0 || step.Metrics.Count > 0 || step.Having.Count > 0;

        if (step.Windows.Count > 0 && isAggregate)
            throw Invalid($"Step '{step.Name}': window functions and GROUP BY/metrics/HAVING " +
                          "cannot share a step; put the window in the next step.");
        if (isAggregate && step.Select.Count > 0)
            throw Invalid($"Step '{step.Name}': select projection is not valid on an aggregate " +
                          "step; project in a following step.");
        if (step.Joins.Count > 0 && step.Select.Count == 0)
            throw Invalid($"Step '{step.Name}': a step with joins requires an explicit select " +
                          "projection to resolve column collisions.");
        if (isAggregate && step.GroupBy.Count == 0)
            throw Invalid($"Step '{step.Name}': metrics/HAVING require at least one GROUP BY key.");

        foreach (var clause in step.Where)
            RequireColumn(step.Name, input.Columns, clause.Property);

        // Join sources — resolution against prior steps or the schema registry.
        var joinSources = ResolveJoinSources(step, earlier, registry);

        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddOutput(string alias)
        {
            if (!output.TryAdd(alias, alias))
                throw Invalid($"Step '{step.Name}': duplicate output alias '{alias}'.");
        }

        if (isAggregate)
        {
            foreach (var key in step.GroupBy)
            {
                RequireColumn(step.Name, input.Columns, key.Field);
                AddOutput(OutputNameFor(key, input.Columns));
            }
            foreach (var m in step.Metrics)
            {
                if (string.IsNullOrEmpty(m.Name))
                    throw Invalid($"Step '{step.Name}': every metric requires an alias.");
                if (!string.IsNullOrEmpty(m.Field))
                    RequireColumn(step.Name, input.Columns, m.Field);
                else if (string.IsNullOrEmpty(m.Expression) && m.Type != AggregationType.Count)
                    throw Invalid($"Step '{step.Name}': metric '{m.Name}' requires a field or expression.");
                AddOutput(m.Name);
            }
            var metricAliases = new Dictionary<string, string>(output, StringComparer.OrdinalIgnoreCase);
            foreach (var h in step.Having)
                RequireColumn(step.Name, metricAliases, h.Property);
            return output;
        }

        // Non-aggregate step.
        if (step.Select.Count > 0)
        {
            foreach (var item in step.Select)
            {
                var source = ResolveSelectSource(step, item, input, joinSources);
                if (item.All)
                {
                    foreach (var col in source.Columns.Values) AddOutput(col);
                }
                else
                {
                    RequireColumn(step.Name, source.Columns, item.Column);
                    AddOutput(string.IsNullOrEmpty(item.Alias) ? source.Columns[item.Column] : item.Alias);
                }
            }
        }
        else
        {
            foreach (var col in input.Columns.Values) AddOutput(col);
        }

        foreach (var w in step.Windows)
        {
            ValidateWindow(step.Name, w, input.Columns);
            AddOutput(w.Alias);
        }

        // Derive sees input columns plus window aliases already added this step.
        var deriveScope = new Dictionary<string, string>(output, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in input.Columns) deriveScope.TryAdd(kv.Key, kv.Value);
        foreach (var d in step.Derive)
        {
            if (string.IsNullOrEmpty(d.Alias) || !IdentifierRx.IsMatch(d.Alias))
                throw Invalid($"Step '{step.Name}': derive alias '{d.Alias}' is not a valid identifier.");
            ValidateDeriveExpr(step.Name, d, deriveScope);
            AddOutput(d.Alias);
            deriveScope.TryAdd(d.Alias, d.Alias);
        }

        return output;
    }

    private static Dictionary<string, StepColumns> ResolveJoinSources(
        PipelineStep step, List<StepColumns> earlier, SchemaRegistry registry)
    {
        var sources = new Dictionary<string, StepColumns>(StringComparer.OrdinalIgnoreCase);
        foreach (var join in step.Joins)
        {
            var prior = earlier.FirstOrDefault(
                s => s.Name.Equals(join.Source, StringComparison.OrdinalIgnoreCase));
            if (prior is not null)
            {
                sources[prior.Name] = prior;
                continue;
            }

            var joinedSchema = registry.Get(join.Source)
                ?? throw Invalid($"Step '{step.Name}': join source '{join.Source}' is neither " +
                                 "an earlier step nor a registered type.");
            var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [joinedSchema.KeyColumn.Name] = joinedSchema.KeyColumn.Name
            };
            foreach (var c in joinedSchema.ScalarColumns) cols[c.Name] = c.Name;
            sources[joinedSchema.TypeName] = new StepColumns(joinedSchema.TypeName, cols);
        }
        return sources;
    }

    private static StepColumns ResolveSelectSource(
        PipelineStep step, SelectItem item, StepColumns input,
        Dictionary<string, StepColumns> joinSources)
    {
        if (string.IsNullOrEmpty(item.Source) ||
            item.Source.Equals(input.Name, StringComparison.OrdinalIgnoreCase))
            return input;

        return joinSources.TryGetValue(item.Source, out var src)
            ? src
            : throw Invalid($"Step '{step.Name}': select source '{item.Source}' is neither the " +
                            "step's input nor one of its join sources.");
    }

    private static void ValidateWindow(
        string stepName, WindowFunction w, Dictionary<string, string> input)
    {
        if (string.IsNullOrEmpty(w.Alias) || !IdentifierRx.IsMatch(w.Alias))
            throw Invalid($"Step '{stepName}': window alias '{w.Alias}' is not a valid identifier.");
        if (string.IsNullOrEmpty(w.OrderBy))
            throw Invalid($"Step '{stepName}': window '{w.Alias}' requires order_by.");
        RequireColumn(stepName, input, w.OrderBy);
        if (!string.IsNullOrEmpty(w.PartitionBy))
            RequireColumn(stepName, input, w.PartitionBy);

        var needsField = w.Kind is WindowFunctionKind.RunningSum or WindowFunctionKind.RunningAvg
            or WindowFunctionKind.Lag or WindowFunctionKind.Lead;
        if (needsField && string.IsNullOrEmpty(w.Field))
            throw Invalid($"Step '{stepName}': window '{w.Alias}' ({w.Kind}) requires a field.");
        if (!string.IsNullOrEmpty(w.Field))
            RequireColumn(stepName, input, w.Field);
    }

    private static void ValidateDeriveExpr(
        string stepName, DeriveColumn d, Dictionary<string, string> available)
    {
        if (d.Expr.Contains(';') || d.Expr.Contains('\'') || d.Expr.Contains('`'))
            throw Invalid($"Step '{stepName}': derive '{d.Alias}' contains a forbidden character " +
                          "(no semicolons, quotes, or backticks).");
        foreach (Match m in TokenRx.Matches(d.Expr))
        {
            if (DeriveWhitelist.Contains(m.Value)) continue;
            if (available.ContainsKey(m.Value)) continue;
            throw Invalid($"Step '{stepName}': derive '{d.Alias}' references '{m.Value}', which is " +
                          "neither an input column nor a whitelisted function.");
        }
    }

    internal static string OutputNameFor(GroupKey key, Dictionary<string, string> input) =>
        key.DateTrunc == DateTrunc.None
            ? input[key.Field]
            : $"{input[key.Field]}_{key.DateTrunc.ToString().ToLowerInvariant()}";

    private static void RequireColumn(
        string stepName, Dictionary<string, string> columns, string property)
    {
        if (string.IsNullOrEmpty(property) || !columns.ContainsKey(property))
            throw Invalid($"Step '{stepName}': unknown column or alias '{property}'.");
    }

    private static RpcException Invalid(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~StarRocksPipelineBuilderTests"`
Expected: PASS (all tests in the file).

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs
git commit -m "feat(server): pipeline pass-1 validation and column tracking"
```

---

### Task 4: Pipeline pass 2 — SQL emission (no joins)

**Files:**
- Modify: `Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs` (add `Build` + emission)
- Test: `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs` (append)

**Interfaces:**
- Consumes: Task 2's `BuildWhere(Func<string,string?>, ..., paramPrefix, out int)` and `BuildHaving(..., paramPrefix)`; Task 3's `TrackAndValidate`, `StepColumns`, `OutputNameFor`.
- Produces (consumed by Tasks 5–7): `internal static (string Sql, DynamicParameters Param) Build(SchemaDescriptor schema, PipelineRequest request, SchemaRegistry registry)`

- [ ] **Step 1: Write the failing golden-SQL tests**

Append to `StarRocksPipelineBuilderTests.cs`:

```csharp
    // ── SQL emission ──────────────────────────────────────────────────────────

    private static string NormalizeWs(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();

    [Fact]
    public void Build_EmptyPipeline_SelectsFromBaseWithDefaultLimit()
    {
        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(), EmptyRegistry());

        NormalizeWs(sql).Should().Be(
            "WITH `base` AS (SELECT * FROM `articles`) SELECT * FROM `base` LIMIT 10000");
    }

    [Fact]
    public void Build_BaseWhere_UsesS0Prefix()
    {
        var request = Request();
        request.BaseWhere.Add(new SearchClause
        {
            Property = "IsPublished", Operator = SearchOperator.Equals,
            Value = new SearchValue { BoolVal = true }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), request, EmptyRegistry());

        sql.Should().Contain("WHERE `IsPublished` = @s0_p0");
        param.Get<bool>("s0_p0").Should().BeTrue();
    }

    [Fact]
    public void Build_WindowThenFilterOnAlias_TopNPerGroup()
    {
        var ranked = new PipelineStep { Name = "ranked" };
        ranked.Windows.Add(new WindowFunction
        {
            Alias = "rn", Kind = WindowFunctionKind.RowNumber,
            PartitionBy = "AuthorId", OrderBy = "PublishedAt", Descending = true
        });

        var top = new PipelineStep { Name = "top5" };
        top.Where.Add(new SearchClause
        {
            Property = "rn", Operator = SearchOperator.LessThanOrEquals,
            Value = new SearchValue { NumberVal = 5 }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(ranked, top), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`ranked` AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY `AuthorId` ORDER BY `PublishedAt` DESC) AS `rn` FROM `base`)");
        n.Should().Contain("`top5` AS (SELECT * FROM `ranked` WHERE `rn` <= @s2_p0)");
        param.Get<double>("s2_p0").Should().Be(5);
    }

    [Fact]
    public void Build_AggregateStep_EmitsGroupByAndHaving()
    {
        var step = new PipelineStep { Name = "by_author" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });
        step.Metrics.Add(new MetricSpec { Name = "avg_wc", Type = AggregationType.Avg, Field = "WordCount" });
        step.Having.Add(new SearchClause
        {
            Property = "articles", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 5 }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(step), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`by_author` AS (SELECT `AuthorId`, COUNT(*) AS `articles`, AVG(`WordCount`) AS `avg_wc` " +
            "FROM `base` GROUP BY `AuthorId` HAVING `articles` > @s1_h0)");
        param.Get<double>("s1_h0").Should().Be(5);
    }

    [Fact]
    public void Build_DateTruncKey_EmitsDateTruncWithAlias()
    {
        var step = new PipelineStep { Name = "monthly" };
        step.GroupBy.Add(new GroupKey { Field = "PublishedAt", DateTrunc = DateTrunc.Month });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(step), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain("DATE_TRUNC('month', `PublishedAt`) AS `PublishedAt_month`");
        n.Should().Contain("GROUP BY `PublishedAt_month`");
    }

    [Fact]
    public void Build_RunningSumAndDerive_EmitOverAndExpression()
    {
        var agg = new PipelineStep { Name = "monthly" };
        agg.GroupBy.Add(new GroupKey { Field = "PublishedAt", DateTrunc = DateTrunc.Month });
        agg.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var w = new PipelineStep { Name = "cume" };
        w.Windows.Add(new WindowFunction
        {
            Alias = "running_total", Kind = WindowFunctionKind.RunningSum,
            Field = "n", OrderBy = "PublishedAt_month"
        });

        var d = new PipelineStep { Name = "share" };
        d.Derive.Add(new DeriveColumn { Alias = "pct", Expr = "100.0 * n / SUM(n) OVER ()" });

        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(agg, w, d), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain("SUM(`n`) OVER (ORDER BY `PublishedAt_month` ASC) AS `running_total`");
        n.Should().Contain("(100.0 * n / SUM(n) OVER ()) AS `pct`");
    }

    [Fact]
    public void Build_ReadsSkipsIntermediateStep()
    {
        var a = new PipelineStep { Name = "a" };
        a.Derive.Add(new DeriveColumn { Alias = "x", Expr = "WordCount + 1" });
        var b = new PipelineStep { Name = "b", Reads = "base" };
        b.Derive.Add(new DeriveColumn { Alias = "y", Expr = "WordCount + 2" });

        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(a, b), EmptyRegistry());

        NormalizeWs(sql).Should().Contain("`b` AS (SELECT *, (WordCount + 2) AS `y` FROM `base`)");
    }

    [Fact]
    public void Build_FinalOrderByAndLimit()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var request = Request(step);
        request.OrderBy.Add(new SearchSort { Property = "n", Descending = true });
        request.Limit = 10;

        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), request, EmptyRegistry());

        NormalizeWs(sql).Should().EndWith("SELECT * FROM `agg` ORDER BY `n` DESC LIMIT 10");
    }

    [Fact]
    public void Build_LagWindow_DefaultsOffsetToOne()
    {
        var step = new PipelineStep { Name = "lagged" };
        step.Windows.Add(new WindowFunction
        {
            Alias = "prev_wc", Kind = WindowFunctionKind.Lag,
            Field = "WordCount", OrderBy = "PublishedAt"
        });

        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(step), EmptyRegistry());

        NormalizeWs(sql).Should().Contain(
            "LAG(`WordCount`, 1) OVER (ORDER BY `PublishedAt` ASC) AS `prev_wc`");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~StarRocksPipelineBuilderTests.Build_"`
Expected: FAIL — `Build` does not exist.

- [ ] **Step 3: Implement emission**

Add to `StarRocksPipelineBuilder.cs` (using `System.Text` and `Dapper` — add the `using` lines):

```csharp
    internal static (string Sql, DynamicParameters Param) Build(
        SchemaDescriptor schema,
        PipelineRequest request,
        SchemaRegistry registry)
    {
        var tracked = TrackAndValidate(schema, request, registry);
        var byName  = tracked.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var param = new DynamicParameters();
        var sb = new StringBuilder();

        var baseWhere = StarRocksQueryBuilder.BuildWhere(
            p => StarRocksQueryBuilder.ResolveColumn(schema, p) is { } c ? $"`{c}`" : null,
            request.BaseWhere, request.BaseLogic, param, "s0_p", out _);
        sb.Append($"WITH `{BaseStepName}` AS (SELECT * FROM `{schema.TableName}`");
        if (baseWhere.Length > 0) sb.Append($" WHERE {baseWhere}");
        sb.Append(')');

        var prev = BaseStepName;
        for (var i = 0; i < request.Steps.Count; i++)
        {
            var step  = request.Steps[i];
            var input = byName[string.IsNullOrEmpty(step.Reads) ? prev : step.Reads];
            sb.Append($", `{step.Name}` AS (");
            EmitStep(sb, step, input, byName, registry, param, stepIdx: i + 1);
            sb.Append(')');
            prev = step.Name;
        }

        sb.Append($" SELECT * FROM `{prev}`");
        var lastCols = byName[prev].Columns;
        if (request.OrderBy.Count > 0)
        {
            var orders = request.OrderBy
                .Select(s => $"`{lastCols[s.Property]}` {(s.Descending ? "DESC" : "ASC")}");
            sb.Append($" ORDER BY {string.Join(", ", orders)}");
        }
        var limit = request.Limit > 0 ? request.Limit : 10_000;
        sb.Append($" LIMIT {limit}");

        return (sb.ToString(), param);
    }

    private static void EmitStep(
        StringBuilder sb,
        PipelineStep step,
        StepColumns input,
        Dictionary<string, StepColumns> byName,
        SchemaRegistry registry,
        DynamicParameters param,
        int stepIdx)
    {
        var isAggregate = step.GroupBy.Count > 0 || step.Metrics.Count > 0;

        var where = StarRocksQueryBuilder.BuildWhere(
            p => input.Columns.TryGetValue(p, out var c) ? $"`{c}`" : null,
            step.Where, step.WhereLogic, param, $"s{stepIdx}_p", out _);

        if (isAggregate)
        {
            var keyExprs  = new List<string>();
            var groupCols = new List<string>();
            foreach (var key in step.GroupBy)
            {
                var col = input.Columns[key.Field];
                if (key.DateTrunc == DateTrunc.None)
                {
                    keyExprs.Add($"`{col}`");
                    groupCols.Add($"`{col}`");
                }
                else
                {
                    var alias = OutputNameFor(key, input.Columns);
                    keyExprs.Add(
                        $"DATE_TRUNC('{key.DateTrunc.ToString().ToLowerInvariant()}', `{col}`) AS `{alias}`");
                    groupCols.Add($"`{alias}`");
                }
            }
            var metricExprs = step.Metrics.Select(m => EmitMetric(m, input.Columns));

            sb.Append($"SELECT {string.Join(", ", keyExprs.Concat(metricExprs))} FROM `{input.Name}`");
            if (where.Length > 0) sb.Append($" WHERE {where}");
            sb.Append($" GROUP BY {string.Join(", ", groupCols)}");

            var having = StarRocksQueryBuilder.BuildHaving(
                step.Having, SearchLogic.And, param, $"s{stepIdx}_h");
            if (having.Length > 0) sb.Append($" HAVING {having}");
            return;
        }

        // Non-aggregate step: projection (+ windows, derives), FROM input (+ joins in Task 5).
        var selectParts = new List<string>();
        if (step.Select.Count > 0)
        {
            foreach (var item in step.Select)
                selectParts.Add(EmitSelectItem(step, item, input, byName, registry));
        }
        else
        {
            selectParts.Add("*");
        }

        foreach (var w in step.Windows)
            selectParts.Add(EmitWindow(w, input.Columns));
        foreach (var d in step.Derive)
            selectParts.Add($"({d.Expr}) AS `{d.Alias}`");

        sb.Append($"SELECT {string.Join(", ", selectParts)} FROM `{input.Name}`");
        if (where.Length > 0) sb.Append($" WHERE {where}");
    }

    private static string EmitMetric(MetricSpec m, Dictionary<string, string> input)
    {
        var quotedName = $"`{m.Name.Replace("`", "``")}`";
        var isCountAll = m.Type == AggregationType.Count
            && string.IsNullOrEmpty(m.Field) && string.IsNullOrEmpty(m.Expression);
        if (isCountAll) return $"COUNT(*) AS {quotedName}";

        var fn = m.Type switch
        {
            AggregationType.Avg   => "AVG",
            AggregationType.Sum   => "SUM",
            AggregationType.Min   => "MIN",
            AggregationType.Max   => "MAX",
            AggregationType.Count => "COUNT",
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Metric '{m.Name}' has unsupported type '{m.Type}'."))
        };
        // m.Expression is raw trusted SQL — same posture as BuildMetricExpr in
        // StarRocksQueryBuilder; see the comment there.
        var arg = !string.IsNullOrEmpty(m.Expression) ? m.Expression : $"`{input[m.Field]}`";
        return $"{fn}({arg}) AS {quotedName}";
    }

    private static string EmitWindow(WindowFunction w, Dictionary<string, string> input)
    {
        var partition = string.IsNullOrEmpty(w.PartitionBy)
            ? ""
            : $"PARTITION BY `{input[w.PartitionBy]}` ";
        var order = $"ORDER BY `{input[w.OrderBy]}` {(w.Descending ? "DESC" : "ASC")}";
        var over  = $"OVER ({partition}{order})";
        var offset = w.Offset > 0 ? w.Offset : 1;

        var call = w.Kind switch
        {
            WindowFunctionKind.RowNumber  => "ROW_NUMBER()",
            WindowFunctionKind.Rank       => "RANK()",
            WindowFunctionKind.DenseRank  => "DENSE_RANK()",
            WindowFunctionKind.RunningSum => $"SUM(`{input[w.Field]}`)",
            WindowFunctionKind.RunningAvg => $"AVG(`{input[w.Field]}`)",
            WindowFunctionKind.Lag        => $"LAG(`{input[w.Field]}`, {offset})",
            WindowFunctionKind.Lead       => $"LEAD(`{input[w.Field]}`, {offset})",
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Window '{w.Alias}' has unsupported kind '{w.Kind}'."))
        };
        return $"{call} {over} AS `{w.Alias}`";
    }

    // Task 5 extends this for join sources; without joins the only legal source is the input.
    private static string EmitSelectItem(
        PipelineStep step, SelectItem item, StepColumns input,
        Dictionary<string, StepColumns> byName, SchemaRegistry registry)
    {
        if (item.All) return "*";
        var col = input.Columns[item.Column];
        return string.IsNullOrEmpty(item.Alias) ? $"`{col}`" : $"`{col}` AS `{item.Alias}`";
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~StarRocksPipelineBuilderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs
git commit -m "feat(server): pipeline pass-2 CTE-chain SQL emission"
```

---

### Task 5: Joins — cross-source validation, ON resolution, projection emission

**Files:**
- Modify: `Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs` (append)

**Interfaces:**
- Consumes: everything from Tasks 3–4.
- Produces: complete `Build` covering joined steps. No signature changes.

- [ ] **Step 1: Write the failing tests**

Append to `StarRocksPipelineBuilderTests.cs`. These need a registry that knows `Author`; extend the fixture area of the file with:

```csharp
    private static SchemaDescriptor AuthorSchemaLocal() => new()
    {
        TypeName      = "Author",
        TableName     = "authors",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [new ColumnDescriptor("Name", "text", false)],
        FkColumns = [], VectorFields = [], ChunkFields = [], Relations = []
    };

    private static SchemaRegistry RegistryWithAuthor()
    {
        var r = EmptyRegistry();
        r.RegisterAsync(AuthorSchemaLocal()).GetAwaiter().GetResult();
        return r;
    }
```

(If `SchemaRegistry.RegisterAsync` has a different name in `Iverson.Api.Schema`, use the method `ObjectSearchGrpcServiceTests` calls — `_registry.RegisterAsync(SchemaFixtures.AuthorSchema())` at line ~67 — and match it.)

Then the tests:

```csharp
    // ── Joins ─────────────────────────────────────────────────────────────────

    private static PipelineStep JoinedStep()
    {
        var step = new PipelineStep { Name = "named" };
        var join = new PipelineJoin { Source = "Author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { All = true });                       // input.*
        step.Select.Add(new SelectItem { Source = "Author", Column = "Name", Alias = "author_name" });
        return step;
    }

    [Fact]
    public void Track_JoinedStep_OutputMergesSelectItems()
    {
        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(JoinedStep()), RegistryWithAuthor());

        cols[1].Columns.Keys.Should().Contain(["Title", "author_name"]);
        cols[1].Columns.Keys.Should().NotContain("Name");   // only exposed via its alias
    }

    [Fact]
    public void Build_JoinAgainstEntityTable_EmitsAliasedJoin()
    {
        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(JoinedStep()), RegistryWithAuthor());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`named` AS (SELECT `base`.*, `Author`.`Name` AS `author_name` FROM `base` " +
            "INNER JOIN `authors` AS `Author` ON `base`.`AuthorId` = `Author`.`Id`)");
    }

    [Fact]
    public void Build_JoinAgainstPriorCte_EmitsCteJoin()
    {
        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "by_author", Kind = JoinKind.Left };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { All = true });
        enriched.Select.Add(new SelectItem { Source = "by_author", Column = "articles" });

        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(agg, enriched), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`enriched` AS (SELECT `base`.*, `by_author`.`articles` FROM `base` " +
            "LEFT JOIN `by_author` ON `base`.`AuthorId` = `by_author`.`AuthorId`)");
    }

    [Fact]
    public void Validate_JoinOnUnknownRightColumn_Throws()
    {
        var step = JoinedStep();
        step.Joins[0].On[0].Right = "Nope";
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor()), "Nope");
    }

    [Fact]
    public void Validate_JoinUnknownSource_Throws()
    {
        var step = JoinedStep();
        step.Joins[0].Source = "Ghost";
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor()), "Ghost");
    }

    [Fact]
    public void Validate_JoinWithoutOnConditions_Throws()
    {
        var step = JoinedStep();
        step.Joins[0].On.Clear();
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor()), "named");
    }

    [Fact]
    public void Validate_JoinForwardStepSource_Throws()
    {
        var joined = new PipelineStep { Name = "j" };
        var join = new PipelineJoin { Source = "later", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        joined.Joins.Add(join);
        joined.Select.Add(new SelectItem { All = true });

        var later = new PipelineStep { Name = "later" };

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(joined, later), EmptyRegistry()), "later");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~StarRocksPipelineBuilderTests" `
Expected: the new join tests FAIL (ON validation and join emission not implemented); all earlier tests still PASS.

- [ ] **Step 3: Implement join validation + emission**

In `ValidateStepAndComputeOutput`, after `ResolveJoinSources(...)`, add ON validation:

```csharp
        foreach (var join in step.Joins)
        {
            if (join.On.Count == 0)
                throw Invalid($"Step '{step.Name}': join to '{join.Source}' requires at least " +
                              "one ON condition.");
            var src = joinSources[join.Source];
            foreach (var cond in join.On)
            {
                RequireColumn(step.Name, input.Columns, cond.Left);
                RequireColumn(step.Name, src.Columns, cond.Right);
            }
        }
```

`ResolveJoinSources` (Task 3) already rejects unknown sources; keying `joinSources` by the *request's* `join.Source` string requires the lookup to be case-insensitive — it is (`OrdinalIgnoreCase` dictionary), but store under `join.Source` canonicalized: change the two `sources[...] = ...` lines to also register the original request spelling:

```csharp
            sources[join.Source] = prior;            // step-CTE branch
            ...
            sources[join.Source] = new StepColumns(joinedSchema.TypeName, cols);   // entity branch
```

Keep `StepColumns.Name` as the canonical CTE/type name (used for SQL aliasing).

In select-item tracking (Task 3's `ResolveSelectSource` handles it already since `joinSources` is passed in), and for `All = true` items whose `Source` names a join source, the expansion is already generic — verify it compiles.

Replace `EmitSelectItem` and extend `EmitStep`'s non-aggregate branch:

```csharp
    private static string EmitSelectItem(
        PipelineStep step, SelectItem item, StepColumns input,
        Dictionary<string, StepColumns> joinSources)
    {
        var hasJoins = step.Joins.Count > 0;

        StepColumns source;
        string sqlAlias;
        if (string.IsNullOrEmpty(item.Source) ||
            item.Source.Equals(input.Name, StringComparison.OrdinalIgnoreCase))
        {
            source = input;
            sqlAlias = input.Name;
        }
        else
        {
            source = joinSources[item.Source];
            sqlAlias = source.Name;
        }

        if (item.All)
            return hasJoins ? $"`{sqlAlias}`.*" : "*";

        var col = source.Columns[item.Column];
        var qualified = hasJoins ? $"`{sqlAlias}`.`{col}`" : $"`{col}`";
        return string.IsNullOrEmpty(item.Alias) ? qualified : $"{qualified} AS `{item.Alias}`";
    }
```

In `EmitStep`, the non-aggregate branch changes to build join sources once and emit JOIN clauses (WHERE-column resolution must also qualify with the input CTE name when joins are present):

```csharp
        // Non-aggregate step.
        var joinSources = ResolveJoinSources(step, byName.Values.ToList(), registry);
        var hasJoins = step.Joins.Count > 0;

        var where = StarRocksQueryBuilder.BuildWhere(
            p => input.Columns.TryGetValue(p, out var c)
                ? hasJoins ? $"`{input.Name}`.`{c}`" : $"`{c}`"
                : null,
            step.Where, step.WhereLogic, param, $"s{stepIdx}_p", out _);

        var selectParts = new List<string>();
        if (step.Select.Count > 0)
            foreach (var item in step.Select)
                selectParts.Add(EmitSelectItem(step, item, input, joinSources));
        else
            selectParts.Add("*");

        foreach (var w in step.Windows)
            selectParts.Add(EmitWindow(w, input.Columns));
        foreach (var d in step.Derive)
            selectParts.Add($"({d.Expr}) AS `{d.Alias}`");

        sb.Append($"SELECT {string.Join(", ", selectParts)} FROM `{input.Name}`");

        foreach (var join in step.Joins)
        {
            var src = joinSources[join.Source];
            var kind = join.Kind switch
            {
                JoinKind.Left  => "LEFT",
                JoinKind.Right => "RIGHT",
                JoinKind.Full  => "FULL",
                _              => "INNER"
            };
            // Entity sources join the physical table aliased as the type name;
            // step sources are CTEs joined by their own name (no alias needed).
            var joinedSchema = registry.Get(src.Name);
            var target = joinedSchema is not null && !byName.ContainsKey(src.Name)
                ? $"`{joinedSchema.TableName}` AS `{src.Name}`"
                : $"`{src.Name}`";
            var conds = join.On.Select(c =>
                $"`{input.Name}`.`{input.Columns[c.Left]}` = `{src.Name}`.`{src.Columns[c.Right]}`");
            sb.Append($" {kind} JOIN {target} ON {string.Join(" AND ", conds)}");
        }

        if (where.Length > 0) sb.Append($" WHERE {where}");
```

Move the earlier `EmitStep` WHERE construction accordingly (it must come after `hasJoins` is known — delete the pre-existing `where` assignment at the top of the non-aggregate branch). Note `ResolveJoinSources` here is called with the *already-tracked* earlier steps: pass `byName.Values.Where(s => !s.Name.Equals(step.Name, StringComparison.OrdinalIgnoreCase)).ToList()` is unnecessary because `byName` at emission time contains all steps — restrict it to steps tracked *before* this one by building the list as emission proceeds, exactly like pass 1 does. Concretely: `Build` maintains `var emitted = new List<StepColumns> { byName[BaseStepName] };`, appends `byName[step.Name]` after each loop iteration, and passes `emitted` into `EmitStep` for `ResolveJoinSources`. (Pass 1 has already validated everything, so this is belt-and-suspenders consistency, not new validation.)

- [ ] **Step 4: Run the whole builder test file**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~StarRocksPipelineBuilderTests"`
Expected: PASS — all tracking, validation, emission, and join tests.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksPipelineBuilderTests.cs
git commit -m "feat(server): pipeline joins against prior CTEs and entity tables"
```

---

### Task 6: gRPC Pipeline method

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (add method after `GroupBy`, ~line 302)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` (append)

**Interfaces:**
- Consumes: `StarRocksPipelineBuilder.Build` (Task 4/5), existing `RequireSchema`, `DictToProtoStruct`, `sr.QueryAsync<dynamic>`, `StarRocksNotReadyException`.
- Produces: `public override async Task Pipeline(PipelineRequest request, IServerStreamWriter<SearchResponse> responseStream, ServerCallContext context)` — the RPC surface Plan 2's clients call.

- [ ] **Step 1: Write the failing tests**

Append to `ObjectSearchGrpcServiceTests.cs`:

```csharp
    // ── Pipeline ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pipeline_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Pipeline(
            new PipelineRequest { TypeName = "Ghost" }, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Pipeline_EmitsWithChain_AndStreamsRows()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedSql = null;
        var fakeRow = new Dictionary<string, object> { ["Name"] = "Alice", ["n"] = 3L };
        _sr.QueryAsync<dynamic>(Arg.Do<string>(s => capturedSql = s), Arg.Any<object?>())
           .Returns(new[] { (dynamic)fakeRow }.AsEnumerable());

        var step = new PipelineStep { Name = "by_name" };
        step.GroupBy.Add(new GroupKey { Field = "Name" });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });
        var request = new PipelineRequest { TypeName = "Author" };
        request.Steps.Add(step);

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        capturedSql.Should().StartWith("WITH `base` AS");
        capturedSql.Should().Contain("`by_name` AS");
        written.Should().HaveCount(1);
        written[0].Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task Pipeline_InvalidStepReference_SurfacesInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var step = new PipelineStep { Name = "s", Reads = "nonexistent" };
        var request = new PipelineRequest { TypeName = "Author" };
        request.Steps.Add(step);

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests.Pipeline_"`
Expected: FAIL — `Pipeline` not overridden (base throws Unimplemented, or compile error if base type mismatch).

- [ ] **Step 3: Implement the method**

Add to `ObjectSearchGrpcService.cs` directly after the `GroupBy` method:

```csharp
    // ── Pipeline (CTE chains) ──────────────────────────────────────────────────

    public override async Task Pipeline(
        PipelineRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Pipeline] type={Type} steps={Steps}",
                request.TypeName, request.Steps.Count);

        var (sql, param) = StarRocksPipelineBuilder.Build(schema, request, registry);

        IEnumerable<dynamic> rows;
        try
        {
            rows = await sr.QueryAsync<dynamic>(sql, param);
        }
        catch (StarRocksNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"StarRocks is not ready: {ex.Message}"));
        }

        foreach (var row in rows)
        {
            var dict = ((IDictionary<string, object>)row)
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            await responseStream.WriteAsync(
                new SearchResponse
                {
                    Data    = DictToProtoStruct(dict),
                    TraceId = request.TraceId
                },
                context.CancellationToken);
        }
    }
```

- [ ] **Step 4: Run the full non-integration suite**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(server): Pipeline gRPC method streaming CTE-chain results"
```

---

### Task 7: Live StarRocks integration tests

**Files:**
- Create: `Iverson.Server/Iverson.Api.Tests/StarRocks/PipelineIntegrationTests.cs`

**Interfaces:**
- Consumes: `StarRocksContainerFixture` from `StarRocksIntegrationTests.cs` (same namespace, `IClassFixture` pattern per that file's line ~129), `StarRocksPipelineBuilder.Build`, `StarRocksRepository.QueryAsync`.
- Produces: end-to-end proof for the spec's motivating scenarios.

- [ ] **Step 1: Write the integration test class**

Create `PipelineIntegrationTests.cs`. Follow `StarRocksIntegrationTests.cs`'s exact conventions for schema/table setup against the fixture's `Repository` (that file registers a table via `ApplyTableAsync` and inserts rows — mirror its setup helper verbatim, adjusting columns). The test data and scenarios:

```csharp
using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.StarRocks;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.StarRocks;

[Trait("Category", "Integration")]
public sealed class PipelineIntegrationTests : IClassFixture<StarRocksContainerFixture>
{
    private readonly StarRocksContainerFixture _fx;

    public PipelineIntegrationTests(StarRocksContainerFixture fx) => _fx = fx;

    private static SchemaDescriptor ArticleSchema() => new()
    {
        TypeName      = "PipeArticle",
        TableName     = "pipe_articles",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns =
        [
            new ColumnDescriptor("AuthorId",    "uuid",        true),
            new ColumnDescriptor("WordCount",   "integer",     true),
            new ColumnDescriptor("PublishedAt", "timestamptz", true),
        ],
        FkColumns = [], VectorFields = [], ChunkFields = [], Relations = []
    };

    private static SchemaRegistry EmptyRegistry()
    {
        var sql = Substitute.For<IPostgresRepository>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        return new SchemaRegistry(sql, NullLogger<SchemaRegistry>.Instance);
    }

    private async Task SeedAsync()
    {
        // 6 rows: author A has 4 articles (WordCount 100/200/300/400, months Jan–Apr 2026),
        // author B has 2 (WordCount 500/600, Jan/Feb 2026). Fixed single-character ids keep
        // assertions deterministic. Idempotent: DROP TABLE IF EXISTS first. StarRocks
        // PRIMARY KEY columns must be NOT NULL and listed first.
        await _fx.Repository.ExecuteAsync("DROP TABLE IF EXISTS pipe_articles");
        await _fx.Repository.ExecuteAsync("""
            CREATE TABLE pipe_articles (
                Id VARCHAR(64) NOT NULL, AuthorId VARCHAR(64), WordCount INT, PublishedAt DATETIME
            ) PRIMARY KEY (Id) DISTRIBUTED BY HASH(Id)
            """);
        await _fx.Repository.ExecuteAsync("""
            INSERT INTO pipe_articles VALUES
            ('1','A',100,'2026-01-01 00:00:00'), ('2','A',200,'2026-02-01 00:00:00'),
            ('3','A',300,'2026-03-01 00:00:00'), ('4','A',400,'2026-04-01 00:00:00'),
            ('5','B',500,'2026-01-15 00:00:00'), ('6','B',600,'2026-02-15 00:00:00')
            """);
    }

    [Fact]
    public async Task TopNPerGroup_ReturnsTwoNewestPerAuthor()
    {
        await SeedAsync();

        var ranked = new PipelineStep { Name = "ranked" };
        ranked.Windows.Add(new WindowFunction
        {
            Alias = "rn", Kind = WindowFunctionKind.RowNumber,
            PartitionBy = "AuthorId", OrderBy = "PublishedAt", Descending = true
        });
        var top = new PipelineStep { Name = "top2" };
        top.Where.Add(new SearchClause
        {
            Property = "rn", Operator = SearchOperator.LessThanOrEquals,
            Value = new SearchValue { NumberVal = 2 }, ClauseType = SearchClauseType.Filter
        });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(ranked);
        request.Steps.Add(top);

        var (sql, param) = StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(4);   // 2 per author
    }

    [Fact]
    public async Task FilterThenAggregateThenHaving_OneQuery()
    {
        await SeedAsync();

        var agg = new PipelineStep { Name = "by_author" };
        agg.Where.Add(new SearchClause
        {
            Property = "WordCount", Operator = SearchOperator.GreaterThanOrEquals,
            Value = new SearchValue { NumberVal = 200 }, ClauseType = SearchClauseType.Filter
        });
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });
        agg.Having.Add(new SearchClause
        {
            Property = "articles", Operator = SearchOperator.GreaterThanOrEquals,
            Value = new SearchValue { NumberVal = 3 }, ClauseType = SearchClauseType.Filter
        });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(agg);

        var (sql, param) = StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(1);   // only author A has ≥3 articles with ≥200 words
        ((string)rows[0].AuthorId).Should().Be("A");
    }

    [Fact]
    public async Task RunningTotal_OverMonthlyBuckets()
    {
        await SeedAsync();

        var monthly = new PipelineStep { Name = "monthly" };
        monthly.GroupBy.Add(new GroupKey { Field = "PublishedAt", DateTrunc = DateTrunc.Month });
        monthly.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var cume = new PipelineStep { Name = "cume" };
        cume.Windows.Add(new WindowFunction
        {
            Alias = "running_total", Kind = WindowFunctionKind.RunningSum,
            Field = "n", OrderBy = "PublishedAt_month"
        });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(monthly);
        request.Steps.Add(cume);
        request.OrderBy.Add(new SearchSort { Property = "PublishedAt_month" });

        var (sql, param) = StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(4);                                  // Jan, Feb, Mar, Apr
        Convert.ToInt64(rows[^1].running_total).Should().Be(6);      // all six articles
    }

    [Fact]
    public async Task DerivedRatio_PercentOfTotal()
    {
        await SeedAsync();

        var byAuthor = new PipelineStep { Name = "by_author" };
        byAuthor.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        byAuthor.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var share = new PipelineStep { Name = "share" };
        share.Derive.Add(new DeriveColumn { Alias = "pct", Expr = "100.0 * n / SUM(n) OVER ()" });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(byAuthor);
        request.Steps.Add(share);

        var (sql, param) = StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Sum(r => (double)Convert.ToDouble(r.pct)).Should().BeApproximately(100.0, 0.01);
    }

    [Fact]
    public async Task JoinCteAgainstBase_EnrichesRowsWithAggregates()
    {
        await SeedAsync();

        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "by_author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { All = true });
        enriched.Select.Add(new SelectItem { Source = "by_author", Column = "articles" });

        var request = new PipelineRequest { TypeName = "PipeArticle" };
        request.Steps.Add(agg);
        request.Steps.Add(enriched);

        var (sql, param) = StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());
        var rows = (await _fx.Repository.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(6);   // every article row, each carrying its author's count
    }
}
```

If `StarRocksRepository` exposes no raw `ExecuteAsync(string)`, use whatever raw-SQL execution member `StarRocksIntegrationTests.cs` uses for its own DDL/seed (mirror it exactly, including any database-selection step in the fixture's connection string).

- [ ] **Step 2: Run the integration tests (requires Docker)**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~PipelineIntegrationTests"`
Expected: PASS (allow several minutes for the StarRocks container to boot; the fixture waits up to 3 minutes).

- [ ] **Step 3: Run the full non-integration suite one final time**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add Iverson.Server/Iverson.Api.Tests/StarRocks/PipelineIntegrationTests.cs
git commit -m "test(server): live StarRocks pipeline integration scenarios"
```
