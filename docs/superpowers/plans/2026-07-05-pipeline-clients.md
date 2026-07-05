# Pipeline CTE — Five Client Builders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the fluent pipeline builder in all five client languages (C#, Java, Python, TypeScript, Go), plus C# `EntityCoordinator` streaming helpers and the documentation section.

**Architecture:** Every language gets a `PipelineBuilder` (base filter + named steps + final sort/limit) and a `PipelineStepBuilder` (where/not, window functions, groupBy+metrics+having, derive, reads, join, select), string-addressed like the existing `GroupByBuilder`s, compiling offline to the `PipelineRequest` proto from the server plan. Client-side pre-validation covers only what needs no schema: step-name uniqueness, reads-must-be-earlier, windows-XOR-aggregation, duplicate step aliases, joins-require-select, metrics-require-groupBy. Responses stream as existing `SearchResponse`, so no new streaming plumbing anywhere.

**Tech Stack:** C# (xUnit/FluentAssertions), Java 17 + protobuf-maven-plugin (JUnit), Python grpcio-tools (pytest), TypeScript ts-proto (vitest), Go protoc (go test).

**Spec:** `docs/superpowers/specs/2026-07-05-pipeline-cte-dsl-design.md`
**Depends on:** `docs/superpowers/plans/2026-07-05-pipeline-server.md` Task 1 (proto contract) being merged — every task here starts by regenerating that language's protos.

## Global Constraints

- Builders never require a live server: `build()`/`Build()` returns the compiled proto.
- Default final limit 10,000; step `reads` empty string means "previous step"; base step is named `base` and its name is reserved.
- Client-side validation failures throw the language's conventional argument error (`ArgumentException` C#, `IllegalArgumentException` Java, `ValueError` Python, `Error` TS) — except Go, which accumulates into the builder's `err` and surfaces it at `Build()`, matching its existing `GroupByBuilder`.
- Method naming follows each language's existing GroupByBuilder conventions exactly (camelCase TS/Java, snake_case Python, PascalCase C#/Go).
- Window `offset` of 0 means "default 1" (the server substitutes 1); builders expose an explicit default of 1.
- The canonical validation set (implement identically in every language):
  1. step name non-empty, unique among steps (case-insensitive), not `base` (case-insensitive)
  2. `reads` must be `base` or an earlier step's name (case-insensitive)
  3. a step with windows must have no groupBy/metrics/having
  4. metrics or having require at least one groupBy key in the same step
  5. a step with joins requires a non-empty select
  6. output aliases within a step (window aliases + derive aliases + metric aliases + select aliases) must be unique (case-insensitive)

---

### Task 1: C# PipelineBuilder + step builder + entry points

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Search/PipelineBuilder.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs` (add `Pipeline` entry)
- Test: Create `Iverson.Clients/DotNet/Iverson.Client.Search.Tests/PipelineBuilderTests.cs`

**Interfaces:**
- Consumes: `PipelineRequest`/`PipelineStep`/`WindowFunction`/`GroupKey`/`DeriveColumn`/`PipelineJoin`/`JoinCondition`/`SelectItem`/`WindowFunctionKind`/`DateTrunc` from `Iverson.Client.Contracts` (server plan Task 1); `SearchValueConverter.ToSearchValue` from this project.
- Produces (consumed by Task 2 and docs):
  - `public static class Pipeline { public static PipelineBuilder For(string typeName); public static PipelineBuilder For<T>() where T : class; }`
  - `public sealed class PipelineBuilder` with `Where(string, SearchOperator, object)`, `Not(...)`, `WithLogic(SearchLogic)`, `Step(string, Action<PipelineStepBuilder>)`, `SortOn(string, bool descending = false)`, `Limit(int)`, `Build(string? traceId = null)`
  - `public sealed class PipelineStepBuilder` and `public sealed class SelectSpecBuilder` per the code below.

- [ ] **Step 1: Write the failing tests**

Create `PipelineBuilderTests.cs`:

```csharp
using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using Xunit;
using static Iverson.Client.Search.SearchOperators;

namespace Iverson.Client.Search.Tests;

public class PipelineBuilderTests
{
    [Fact]
    public void Build_FullPipeline_CompilesToExpectedProto()
    {
        var request = Pipeline.For("Article")
            .Where("IsPublished", EqualTo, true)
            .Step("by_author", s => s
                .GroupBy("AuthorId")
                .CountAll("articles")
                .Having("articles", GreaterThan, 5))
            .Step("ranked", s => s
                .RowNumber("rank", orderBy: "articles", descending: true))
            .Step("named", s => s
                .Join("Author", ("AuthorId", "Id"))
                .Select(p => p.AllFrom("ranked").Pick("Author", "Name", "author_name")))
            .SortOn("rank")
            .Limit(5)
            .Build();

        request.TypeName.Should().Be("Article");
        request.BaseWhere.Should().HaveCount(1);
        request.BaseWhere[0].Property.Should().Be("IsPublished");
        request.Steps.Should().HaveCount(3);

        var agg = request.Steps[0];
        agg.Name.Should().Be("by_author");
        agg.GroupBy.Should().ContainSingle(k => k.Field == "AuthorId" && k.DateTrunc == DateTrunc.None);
        agg.Metrics.Should().ContainSingle(m => m.Name == "articles" && m.Type == AggregationType.Count);
        agg.Having.Should().ContainSingle(h => h.Property == "articles");

        var win = request.Steps[1];
        win.Windows.Should().ContainSingle(w =>
            w.Alias == "rank" && w.Kind == WindowFunctionKind.RowNumber &&
            w.OrderBy == "articles" && w.Descending);

        var joined = request.Steps[2];
        joined.Joins.Should().ContainSingle(j => j.Source == "Author" && j.Kind == JoinKind.Inner);
        joined.Joins[0].On.Should().ContainSingle(c => c.Left == "AuthorId" && c.Right == "Id");
        joined.Select.Should().HaveCount(2);
        joined.Select[0].All.Should().BeTrue();
        joined.Select[0].Source.Should().Be("ranked");
        joined.Select[1].Alias.Should().Be("author_name");

        request.OrderBy.Should().ContainSingle(o => o.Property == "rank");
        request.Limit.Should().Be(5);
    }

    [Fact]
    public void Build_Typed_UsesTypeName()
    {
        var request = Pipeline.For<PipelineBuilderTests>().Build();
        request.TypeName.Should().Be(nameof(PipelineBuilderTests));
        request.Limit.Should().Be(10_000);
    }

    [Fact]
    public void Step_ExplicitReads_IsCarried()
    {
        var request = Pipeline.For("Article")
            .Step("a", s => s.Derive("x", "WordCount + 1"))
            .Step("b", s => s.Reads("base").Derive("y", "WordCount + 2"))
            .Build();

        request.Steps[1].Reads.Should().Be("base");
    }

    [Fact]
    public void Step_DuplicateName_Throws()
    {
        var act = () => Pipeline.For("Article")
            .Step("x", s => s.Derive("a", "WordCount"))
            .Step("X", s => s.Derive("b", "WordCount"));
        act.Should().Throw<ArgumentException>().WithMessage("*X*");
    }

    [Fact]
    public void Step_ReadsUnknownStep_Throws()
    {
        var act = () => Pipeline.For("Article")
            .Step("a", s => s.Reads("nope").Derive("x", "WordCount"));
        act.Should().Throw<ArgumentException>().WithMessage("*nope*");
    }

    [Fact]
    public void Step_WindowAndGroupBy_Throws()
    {
        var act = () => Pipeline.For("Article")
            .Step("bad", s => s
                .RowNumber("rn", orderBy: "Id")
                .GroupBy("AuthorId").CountAll());
        act.Should().Throw<ArgumentException>().WithMessage("*bad*");
    }

    [Fact]
    public void Step_MetricsWithoutGroupBy_Throws()
    {
        var act = () => Pipeline.For("Article")
            .Step("bad", s => s.CountAll("n"));
        act.Should().Throw<ArgumentException>().WithMessage("*bad*");
    }

    [Fact]
    public void Step_JoinWithoutSelect_Throws()
    {
        var act = () => Pipeline.For("Article")
            .Step("bad", s => s.Join("Author", ("AuthorId", "Id")));
        act.Should().Throw<ArgumentException>().WithMessage("*select*");
    }

    [Fact]
    public void Step_DuplicateAliases_Throws()
    {
        var act = () => Pipeline.For("Article")
            .Step("bad", s => s
                .RowNumber("x", orderBy: "Id")
                .Derive("X", "WordCount + 1"));
        act.Should().Throw<ArgumentException>().WithMessage("*X*");
    }

    [Fact]
    public void Windows_AllKinds_MapToProtoKinds()
    {
        var request = Pipeline.For("Article")
            .Step("w", s => s
                .RowNumber("a", orderBy: "Id")
                .Rank("b", orderBy: "Id")
                .DenseRank("c", orderBy: "Id")
                .RunningSum("d", "WordCount", orderBy: "Id")
                .RunningAvg("e", "WordCount", orderBy: "Id")
                .Lag("f", "WordCount", orderBy: "Id", offset: 2)
                .Lead("g", "WordCount", orderBy: "Id"))
            .Build();

        var kinds = request.Steps[0].Windows.Select(w => w.Kind).ToList();
        kinds.Should().Equal(
            WindowFunctionKind.RowNumber, WindowFunctionKind.Rank, WindowFunctionKind.DenseRank,
            WindowFunctionKind.RunningSum, WindowFunctionKind.RunningAvg,
            WindowFunctionKind.Lag, WindowFunctionKind.Lead);
        request.Steps[0].Windows[5].Offset.Should().Be(2);
        request.Steps[0].Windows[6].Offset.Should().Be(1);
    }

    [Fact]
    public void GroupBy_WithDateTrunc_SetsEnum()
    {
        var request = Pipeline.For("Article")
            .Step("m", s => s.GroupBy("PublishedAt", DateTrunc.Month).CountAll("n"))
            .Build();

        request.Steps[0].GroupBy[0].DateTrunc.Should().Be(DateTrunc.Month);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj --filter "FullyQualifiedName~PipelineBuilderTests"`
Expected: FAIL — `Pipeline`/`PipelineBuilder` do not exist.

- [ ] **Step 3: Implement**

Add to `Query.cs`:

```csharp
    /// <summary>
    /// Entry point for the pipeline (CTE chain) DSL. String-based like GroupBy —
    /// steps and joins bring multiple sources into scope.
    /// </summary>
    public static PipelineBuilder Pipeline(string typeName) => new(typeName);
```

Create `PipelineBuilder.cs`:

```csharp
using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>Entry point for the pipeline (CTE chain) DSL.</summary>
public static class Pipeline
{
    public static PipelineBuilder For(string typeName) => new(typeName);
    public static PipelineBuilder For<T>() where T : class => new(typeof(T).Name);
}

/// <summary>
/// Fluent DSL builder that compiles to a <see cref="PipelineRequest"/>. Each
/// <see cref="Step"/> is exactly one CTE in the generated StarRocks query; steps read
/// the previous step by default or any earlier step via <see cref="PipelineStepBuilder.Reads"/>.
/// String-addressed like <see cref="GroupByBuilder"/> — pipelines put multiple sources in scope.
/// </summary>
public sealed class PipelineBuilder
{
    internal const string BaseStepName = "base";

    private readonly string _typeName;
    private readonly List<SearchClause> _baseWhere = [];
    private readonly List<PipelineStep> _steps     = [];
    private readonly List<SearchSort>   _orderBy   = [];
    private SearchLogic _baseLogic = SearchLogic.And;
    private int         _limit     = 10_000;

    internal PipelineBuilder(string typeName) => _typeName = typeName;

    // ── Base-step filter ────────────────────────────────────────────────────────

    public PipelineBuilder Where(string field, SearchOperator op, object value)
        => AddBaseClause(field, op, value, SearchClauseType.Filter);

    public PipelineBuilder Not(string field, SearchOperator op, object value)
        => AddBaseClause(field, op, value, SearchClauseType.MustNot);

    public PipelineBuilder WithLogic(SearchLogic logic)
    {
        _baseLogic = logic;
        return this;
    }

    // ── Steps ───────────────────────────────────────────────────────────────────

    public PipelineBuilder Step(string name, Action<PipelineStepBuilder> configure)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Step name must be non-empty.");
        if (name.Equals(BaseStepName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Step name '{name}' is reserved for the implicit base step.");
        if (_steps.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Duplicate step name '{name}'.");

        var earlier = _steps.Select(s => s.Name).Prepend(BaseStepName).ToList();
        var builder = new PipelineStepBuilder(name, earlier);
        configure(builder);
        _steps.Add(builder.BuildStep());
        return this;
    }

    // ── Final ordering and paging ───────────────────────────────────────────────

    public PipelineBuilder SortOn(string field, bool descending = false)
    {
        _orderBy.Add(new SearchSort { Property = field, Descending = descending });
        return this;
    }

    public PipelineBuilder Limit(int n)
    {
        _limit = n;
        return this;
    }

    // ── Build ───────────────────────────────────────────────────────────────────

    public PipelineRequest Build(string? traceId = null)
    {
        var request = new PipelineRequest
        {
            TypeName  = _typeName,
            BaseLogic = _baseLogic,
            Limit     = _limit,
            TraceId   = traceId ?? string.Empty
        };
        request.BaseWhere.AddRange(_baseWhere);
        request.Steps.AddRange(_steps);
        request.OrderBy.AddRange(_orderBy);
        return request;
    }

    private PipelineBuilder AddBaseClause(
        string field, SearchOperator op, object value, SearchClauseType clauseType)
    {
        _baseWhere.Add(new SearchClause
        {
            Property   = field,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = clauseType
        });
        return this;
    }
}

/// <summary>
/// Builds one pipeline step (= one CTE): WHERE + (window functions XOR
/// GROUP BY/metrics/HAVING) + derived columns + joins + projection.
/// </summary>
public sealed class PipelineStepBuilder
{
    private readonly string _name;
    private readonly IReadOnlyList<string> _earlierSteps;
    private readonly PipelineStep _step;

    internal PipelineStepBuilder(string name, IReadOnlyList<string> earlierSteps)
    {
        _name         = name;
        _earlierSteps = earlierSteps;
        _step         = new PipelineStep { Name = name };
    }

    // ── Input selection ─────────────────────────────────────────────────────────

    /// <summary>Reads any earlier named step (or "base") instead of the previous step.</summary>
    public PipelineStepBuilder Reads(string stepName)
    {
        if (!_earlierSteps.Any(s => s.Equals(stepName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                $"Step '{_name}': reads '{stepName}' does not name an earlier step.");
        _step.Reads = stepName;
        return this;
    }

    // ── Filtering ───────────────────────────────────────────────────────────────

    public PipelineStepBuilder Where(string field, SearchOperator op, object value)
        => AddClause(field, op, value, SearchClauseType.Filter);

    public PipelineStepBuilder Not(string field, SearchOperator op, object value)
        => AddClause(field, op, value, SearchClauseType.MustNot);

    public PipelineStepBuilder WithLogic(SearchLogic logic)
    {
        _step.WhereLogic = logic;
        return this;
    }

    // ── Window functions ────────────────────────────────────────────────────────

    public PipelineStepBuilder RowNumber(
        string alias, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.RowNumber, "", orderBy, descending, partitionBy);

    public PipelineStepBuilder Rank(
        string alias, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.Rank, "", orderBy, descending, partitionBy);

    public PipelineStepBuilder DenseRank(
        string alias, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.DenseRank, "", orderBy, descending, partitionBy);

    public PipelineStepBuilder RunningSum(
        string alias, string field, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.RunningSum, field, orderBy, descending, partitionBy);

    public PipelineStepBuilder RunningAvg(
        string alias, string field, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.RunningAvg, field, orderBy, descending, partitionBy);

    public PipelineStepBuilder Lag(
        string alias, string field, string orderBy,
        int offset = 1, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.Lag, field, orderBy, descending, partitionBy, offset);

    public PipelineStepBuilder Lead(
        string alias, string field, string orderBy,
        int offset = 1, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.Lead, field, orderBy, descending, partitionBy, offset);

    // ── Aggregation ─────────────────────────────────────────────────────────────

    public PipelineStepBuilder GroupBy(string field, DateTrunc dateTrunc = DateTrunc.None)
    {
        _step.GroupBy.Add(new GroupKey { Field = field, DateTrunc = dateTrunc });
        return this;
    }

    public PipelineStepBuilder Sum(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_sum", AggregationType.Sum, field: field);
    public PipelineStepBuilder Avg(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_avg", AggregationType.Avg, field: field);
    public PipelineStepBuilder Min(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_min", AggregationType.Min, field: field);
    public PipelineStepBuilder Max(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_max", AggregationType.Max, field: field);
    public PipelineStepBuilder Count(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_count", AggregationType.Count, field: field);
    public PipelineStepBuilder CountAll(string alias = "count")
        => AddMetric(alias, AggregationType.Count);
    public PipelineStepBuilder SumExpr(string expression, string alias)
        => AddMetric(alias, AggregationType.Sum, expression: expression);
    public PipelineStepBuilder AvgExpr(string expression, string alias)
        => AddMetric(alias, AggregationType.Avg, expression: expression);

    public PipelineStepBuilder Having(string alias, SearchOperator op, object value)
    {
        _step.Having.Add(new SearchClause
        {
            Property   = alias,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = SearchClauseType.Filter
        });
        return this;
    }

    // ── Derived columns ─────────────────────────────────────────────────────────

    public PipelineStepBuilder Derive(string alias, string expr)
    {
        _step.Derive.Add(new DeriveColumn { Alias = alias, Expr = expr });
        return this;
    }

    // ── Joins and projection ────────────────────────────────────────────────────

    /// <summary>Joins the step's input against an earlier step's CTE or a registered entity type.</summary>
    public PipelineStepBuilder Join(
        string source, (string Left, string Right) on, JoinKind kind = JoinKind.Inner)
        => Join(source, [on], kind);

    public PipelineStepBuilder Join(
        string source, IReadOnlyList<(string Left, string Right)> on, JoinKind kind = JoinKind.Inner)
    {
        var join = new PipelineJoin { Source = source, Kind = kind };
        foreach (var (left, right) in on)
            join.On.Add(new JoinCondition { Left = left, Right = right });
        _step.Joins.Add(join);
        return this;
    }

    public PipelineStepBuilder Select(Action<SelectSpecBuilder> configure)
    {
        var builder = new SelectSpecBuilder();
        configure(builder);
        _step.Select.AddRange(builder.Items);
        return this;
    }

    // ── Build + validation ──────────────────────────────────────────────────────

    internal PipelineStep BuildStep()
    {
        var isAggregate = _step.GroupBy.Count > 0 || _step.Metrics.Count > 0 || _step.Having.Count > 0;

        if (_step.Windows.Count > 0 && isAggregate)
            throw new ArgumentException(
                $"Step '{_name}': window functions and GROUP BY/metrics/HAVING cannot share a step.");
        if ((_step.Metrics.Count > 0 || _step.Having.Count > 0) && _step.GroupBy.Count == 0)
            throw new ArgumentException(
                $"Step '{_name}': metrics/HAVING require at least one GroupBy key.");
        if (_step.Joins.Count > 0 && _step.Select.Count == 0)
            throw new ArgumentException(
                $"Step '{_name}': a step with joins requires a select projection.");

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _step.Windows.Select(w => w.Alias)
                     .Concat(_step.Derive.Select(d => d.Alias))
                     .Concat(_step.Metrics.Select(m => m.Name))
                     .Concat(_step.Select.Where(s => !string.IsNullOrEmpty(s.Alias)).Select(s => s.Alias)))
        {
            if (!aliases.Add(a))
                throw new ArgumentException($"Step '{_name}': duplicate output alias '{a}'.");
        }

        return _step;
    }

    private PipelineStepBuilder AddClause(
        string field, SearchOperator op, object value, SearchClauseType clauseType)
    {
        _step.Where.Add(new SearchClause
        {
            Property   = field,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = clauseType
        });
        return this;
    }

    private PipelineStepBuilder AddWindow(
        string alias, WindowFunctionKind kind, string field, string orderBy,
        bool descending, string? partitionBy, int offset = 1)
    {
        _step.Windows.Add(new WindowFunction
        {
            Alias       = alias,
            Kind        = kind,
            Field       = field,
            OrderBy     = orderBy,
            Descending  = descending,
            PartitionBy = partitionBy ?? string.Empty,
            Offset      = offset
        });
        return this;
    }

    private PipelineStepBuilder AddMetric(
        string alias, AggregationType type, string? field = null, string? expression = null)
    {
        var spec = new MetricSpec { Name = alias, Type = type };
        if (field is not null)      spec.Field      = field;
        if (expression is not null) spec.Expression = expression;
        _step.Metrics.Add(spec);
        return this;
    }
}

/// <summary>Builds a joined step's projection: which columns survive the join.</summary>
public sealed class SelectSpecBuilder
{
    internal List<SelectItem> Items { get; } = [];

    /// <summary>All columns from a source ("base", a step name, or a joined type name).</summary>
    public SelectSpecBuilder AllFrom(string source)
    {
        Items.Add(new SelectItem { Source = source, All = true });
        return this;
    }

    /// <summary>One column from a source, optionally renamed.</summary>
    public SelectSpecBuilder Pick(string source, string column, string? alias = null)
    {
        Items.Add(new SelectItem
        {
            Source = source,
            Column = column,
            Alias  = alias ?? string.Empty
        });
        return this;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj`
Expected: PASS (new pipeline tests plus existing QueryBuilder/GroupByBuilder tests).

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/DotNet/Iverson.Client.Search/PipelineBuilder.cs Iverson.Clients/DotNet/Iverson.Client.Search/Query.cs Iverson.Clients/DotNet/Iverson.Client.Search.Tests/PipelineBuilderTests.cs
git commit -m "feat(dotnet): pipeline CTE builder"
```

---

### Task 2: C# EntityCoordinator streaming helpers

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs` (append after `SearchAsync`, ~line 202)
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/StructConverter.cs` (add `ToDictionary`)
- Test: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/` — add `PipelineCoordinatorTests.cs` following the existing coordinator test conventions in that project (substitute the gRPC client the same way existing tests there substitute `ObjectSearchService.ObjectSearchServiceClient`; if that project has no search-client test precedent, test `StructConverter.ToDictionary` directly and cover the coordinator methods in the sample app instead — do NOT invent a new gRPC mocking harness for this task).

**Interfaces:**
- Consumes: `PipelineBuilder.Build()` (Task 1), `search.Pipeline(request, cancellationToken)` generated stub (server plan Task 1).
- Produces:
  - `public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> PipelineAsync(PipelineBuilder pipeline, CancellationToken ct = default)`
  - `public async IAsyncEnumerable<TResult> PipelineAsync<TResult>(PipelineBuilder pipeline, CancellationToken ct = default) where TResult : class`
  - `internal static IReadOnlyDictionary<string, object?> StructConverter.ToDictionary(Struct data)`

- [ ] **Step 1: Write the failing test for StructConverter.ToDictionary**

In `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/`, create `StructConverterToDictionaryTests.cs`:

```csharp
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Iverson.Client.Core;
using Xunit;

namespace Iverson.Client.Core.Tests;

public class StructConverterToDictionaryTests
{
    [Fact]
    public void ToDictionary_MapsScalarKinds()
    {
        var s = new Struct
        {
            Fields =
            {
                ["name"]  = Value.ForString("Alice"),
                ["n"]     = Value.ForNumber(3),
                ["flag"]  = Value.ForBool(true),
                ["blank"] = Value.ForNull()
            }
        };

        var dict = StructConverter.ToDictionary(s);

        dict["name"].Should().Be("Alice");
        dict["n"].Should().Be(3.0);
        dict["flag"].Should().Be(true);
        dict["blank"].Should().BeNull();
    }

    [Fact]
    public void ToDictionary_MapsNestedListAndStruct()
    {
        var s = new Struct
        {
            Fields =
            {
                ["tags"]  = Value.ForList(Value.ForString("a"), Value.ForString("b")),
                ["inner"] = Value.ForStruct(new Struct { Fields = { ["x"] = Value.ForNumber(1) } })
            }
        };

        var dict = StructConverter.ToDictionary(s);

        dict["tags"].Should().BeEquivalentTo(new object?[] { "a", "b" });
        ((IReadOnlyDictionary<string, object?>)dict["inner"]!)["x"].Should().Be(1.0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj --filter "FullyQualifiedName~StructConverterToDictionaryTests"`
Expected: FAIL — no `ToDictionary`.

- [ ] **Step 3: Implement**

Append to `StructConverter.cs`:

```csharp
    /// <summary>
    /// Converts a Struct row (e.g. a Pipeline/GroupBy result) to a string-keyed dictionary
    /// without forcing it through a typed POCO.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ToDictionary(Struct data) =>
        data.Fields.ToDictionary(kv => kv.Key, kv => FromValue(kv.Value));

    private static object? FromValue(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.StringValue => v.StringValue,
        Value.KindOneofCase.NumberValue => v.NumberValue,
        Value.KindOneofCase.BoolValue   => v.BoolValue,
        Value.KindOneofCase.ListValue   => v.ListValue.Values.Select(FromValue).ToList(),
        Value.KindOneofCase.StructValue => ToDictionary(v.StructValue),
        _                               => null
    };
```

Append to `EntityCoordinator.cs` after `SearchAsync`:

```csharp
    /// <summary>
    /// Executes a pipeline (CTE chain) and streams untyped rows. Column set depends on the
    /// pipeline's final step, so rows come back as string-keyed dictionaries.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> PipelineAsync(
        PipelineBuilder pipeline,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request     = pipeline.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.Pipeline {Entity} ({Steps} steps)",
            _descriptor.EntityName, request.Steps.Count);

        var stream = search.Pipeline(request, cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
            yield return StructConverter.ToDictionary(response.Data);
    }

    /// <summary>
    /// Executes a pipeline and projects each row onto <typeparamref name="TResult"/>
    /// (any class whose property names match the pipeline's output aliases).
    /// </summary>
    public async IAsyncEnumerable<TResult> PipelineAsync<TResult>(
        PipelineBuilder pipeline,
        [EnumeratorCancellation] CancellationToken ct = default)
        where TResult : class
    {
        var request     = pipeline.Build();
        request.TraceId = CurrentTraceId();

        logger.LogDebug("ObjectSearch.Pipeline {Entity} ({Steps} steps, typed)",
            _descriptor.EntityName, request.Steps.Count);

        var stream = search.Pipeline(request, cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            var row = StructConverter.FromStruct<TResult>(response.Data);
            if (row is not null) yield return row;
        }
    }
```

Note: `StructConverter.ToDictionary` must be `public` (the coordinator returns its output type); change the class's `internal static class` declaration ONLY if it is not already visible — `EntityCoordinator` is in the same assembly, and the dictionaries it returns are plain BCL types, so `internal` visibility of the converter itself is fine. Keep `StructConverter` internal.

- [ ] **Step 4: Run the DotNet client test suites and build the sample**

Run: `dotnet build Iverson.Clients/DotNet/Iverson.Client.slnx && dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj && dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs Iverson.Clients/DotNet/Iverson.Client.Core/StructConverter.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/StructConverterToDictionaryTests.cs
git commit -m "feat(dotnet): EntityCoordinator.PipelineAsync streaming helpers"
```

---

### Task 3: Java pipeline builder

**Files:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/PipelineBuilder.java`
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/PipelineStepBuilder.java`
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/SelectSpecBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java` (add `pipeline` factory)
- Test: Create `Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/PipelineBuilderTest.java`

**Interfaces:**
- Consumes: generated `iverson.ObjectSearch.PipelineRequest/PipelineStep/WindowFunction/GroupKey/DeriveColumn/PipelineJoin/JoinCondition/SelectItem/WindowFunctionKind/DateTrunc` (regenerated by `mvn compile` from the shared proto), `SearchValues.toSearchValue`.
- Produces: `Query.pipeline(String)` → `PipelineBuilder` with `where/not/withLogic/step/sortOn/sortOnDesc/limit/build`, step builder mirroring Task 1's C# surface in camelCase with `reads(...)` (never `from` — Java keyword).

- [ ] **Step 1: Regenerate protos and confirm the new types exist**

Run: `cd Iverson.Clients/Java/client && mvn -q compile`
Expected: BUILD SUCCESS; `target/generated-sources` contains `PipelineRequest`.

- [ ] **Step 2: Write the failing tests**

Create `PipelineBuilderTest.java`:

```java
package io.iverson.client.search;

import iverson.ObjectSearch.AggregationType;
import iverson.ObjectSearch.DateTrunc;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.PipelineRequest;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.WindowFunctionKind;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class PipelineBuilderTest {

    @Test
    void buildFullPipelineCompilesToExpectedProto() {
        PipelineRequest req = Query.pipeline("Article")
            .where("IsPublished", SearchOperator.EQUALS, true)
            .step("by_author", s -> s
                .groupBy("AuthorId")
                .countAll("articles")
                .having("articles", SearchOperator.GREATER_THAN, 5))
            .step("ranked", s -> s
                .rowNumber("rank", "articles", true))
            .step("named", s -> s
                .join("Author", "AuthorId", "Id")
                .select(sel -> sel.allFrom("ranked").pick("Author", "Name", "author_name")))
            .sortOnDesc("rank")
            .limit(5)
            .build();

        assertEquals("Article", req.getTypeName());
        assertEquals(1, req.getBaseWhereCount());
        assertEquals(3, req.getStepsCount());

        var agg = req.getSteps(0);
        assertEquals("by_author", agg.getName());
        assertEquals("AuthorId", agg.getGroupBy(0).getField());
        assertEquals(DateTrunc.NONE, agg.getGroupBy(0).getDateTrunc());
        assertEquals(AggregationType.COUNT, agg.getMetrics(0).getType());
        assertEquals("articles", agg.getHaving(0).getProperty());

        var win = req.getSteps(1);
        assertEquals(WindowFunctionKind.ROW_NUMBER, win.getWindows(0).getKind());
        assertTrue(win.getWindows(0).getDescending());

        var joined = req.getSteps(2);
        assertEquals("Author", joined.getJoins(0).getSource());
        assertEquals(JoinKind.INNER, joined.getJoins(0).getKind());
        assertEquals("AuthorId", joined.getJoins(0).getOn(0).getLeft());
        assertTrue(joined.getSelect(0).getAll());
        assertEquals("author_name", joined.getSelect(1).getAlias());

        assertEquals(5, req.getLimit());
    }

    @Test
    void duplicateStepNameThrows() {
        var b = Query.pipeline("Article").step("x", s -> s.derive("a", "WordCount"));
        assertThrows(IllegalArgumentException.class,
            () -> b.step("X", s -> s.derive("b", "WordCount")));
    }

    @Test
    void readsUnknownStepThrows() {
        assertThrows(IllegalArgumentException.class,
            () -> Query.pipeline("Article").step("a", s -> s.reads("nope")));
    }

    @Test
    void windowAndGroupByInOneStepThrows() {
        assertThrows(IllegalArgumentException.class,
            () -> Query.pipeline("Article").step("bad", s -> s
                .rowNumber("rn", "Id", false)
                .groupBy("AuthorId").countAll("n")));
    }

    @Test
    void joinWithoutSelectThrows() {
        assertThrows(IllegalArgumentException.class,
            () -> Query.pipeline("Article").step("bad", s -> s.join("Author", "AuthorId", "Id")));
    }

    @Test
    void duplicateAliasesThrow() {
        assertThrows(IllegalArgumentException.class,
            () -> Query.pipeline("Article").step("bad", s -> s
                .rowNumber("x", "Id", false)
                .derive("X", "WordCount + 1")));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `cd Iverson.Clients/Java/client && mvn -q test -Dtest=PipelineBuilderTest`
Expected: compile FAILURE (no `Query.pipeline`).

- [ ] **Step 4: Implement**

Add to `Query.java`:

```java
    /**
     * Creates a {@link PipelineBuilder} scoped to the given type name. Pipelines compile
     * fluent step chains into one server-side CTE-chain query.
     */
    public static PipelineBuilder pipeline(String typeName) {
        return new PipelineBuilder(typeName);
    }
```

Create `PipelineBuilder.java`:

```java
package io.iverson.client.search;

import iverson.ObjectSearch.PipelineRequest;
import iverson.ObjectSearch.PipelineStep;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SearchSort;

import java.util.ArrayList;
import java.util.List;
import java.util.function.Consumer;

/**
 * Fluent DSL builder that compiles to a {@link PipelineRequest}. Each {@code step(...)} is
 * exactly one CTE in the generated StarRocks query; steps read the previous step by default
 * or any earlier step via {@link PipelineStepBuilder#reads(String)}. String-addressed like
 * {@link GroupByBuilder}. Instantiate via {@link Query#pipeline(String)}.
 */
public final class PipelineBuilder {

    static final String BASE_STEP_NAME = "base";

    private final String typeName;
    private final List<SearchClause> baseWhere = new ArrayList<>();
    private final List<PipelineStep> steps     = new ArrayList<>();
    private final List<SearchSort>   orderBy   = new ArrayList<>();
    private SearchLogic baseLogic = SearchLogic.AND;
    private int         limit     = 10_000;

    PipelineBuilder(String typeName) {
        this.typeName = typeName;
    }

    /** Adds a WHERE (FILTER) clause on the implicit base step. */
    public PipelineBuilder where(String field, SearchOperator op, Object value) {
        return addBaseClause(field, op, value, SearchClauseType.FILTER);
    }

    /** Adds a MUST_NOT clause on the implicit base step. */
    public PipelineBuilder not(String field, SearchOperator op, Object value) {
        return addBaseClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    /** Sets the logic combining base-step WHERE clauses. Default: AND. */
    public PipelineBuilder withLogic(SearchLogic logic) {
        this.baseLogic = logic;
        return this;
    }

    /** Adds one named step (= one CTE). */
    public PipelineBuilder step(String name, Consumer<PipelineStepBuilder> configure) {
        if (name == null || name.isEmpty())
            throw new IllegalArgumentException("Step name must be non-empty.");
        if (name.equalsIgnoreCase(BASE_STEP_NAME))
            throw new IllegalArgumentException(
                "Step name '" + name + "' is reserved for the implicit base step.");
        for (PipelineStep s : steps)
            if (s.getName().equalsIgnoreCase(name))
                throw new IllegalArgumentException("Duplicate step name '" + name + "'.");

        List<String> earlier = new ArrayList<>();
        earlier.add(BASE_STEP_NAME);
        for (PipelineStep s : steps) earlier.add(s.getName());

        PipelineStepBuilder builder = new PipelineStepBuilder(name, earlier);
        configure.accept(builder);
        steps.add(builder.buildStep());
        return this;
    }

    /** Final ORDER BY on the last step's output. */
    public PipelineBuilder sortOn(String field) {
        return sortOn(field, false);
    }

    public PipelineBuilder sortOnDesc(String field) {
        return sortOn(field, true);
    }

    public PipelineBuilder sortOn(String field, boolean descending) {
        orderBy.add(SearchSort.newBuilder().setProperty(field).setDescending(descending).build());
        return this;
    }

    /** Final row limit. Default: 10000. */
    public PipelineBuilder limit(int n) {
        this.limit = n;
        return this;
    }

    /** Compiles to the {@link PipelineRequest} proto. */
    public PipelineRequest build() {
        return build("");
    }

    public PipelineRequest build(String traceId) {
        return PipelineRequest.newBuilder()
            .setTypeName(typeName)
            .addAllBaseWhere(baseWhere)
            .setBaseLogic(baseLogic)
            .addAllSteps(steps)
            .addAllOrderBy(orderBy)
            .setLimit(limit)
            .setTraceId(traceId == null ? "" : traceId)
            .build();
    }

    private PipelineBuilder addBaseClause(
            String field, SearchOperator op, Object value, SearchClauseType clauseType) {
        baseWhere.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(clauseType)
            .build());
        return this;
    }
}
```

Create `PipelineStepBuilder.java`:

```java
package io.iverson.client.search;

import iverson.ObjectSearch.AggregationType;
import iverson.ObjectSearch.DateTrunc;
import iverson.ObjectSearch.DeriveColumn;
import iverson.ObjectSearch.GroupKey;
import iverson.ObjectSearch.JoinCondition;
import iverson.ObjectSearch.JoinKind;
import iverson.ObjectSearch.MetricSpec;
import iverson.ObjectSearch.PipelineJoin;
import iverson.ObjectSearch.PipelineStep;
import iverson.ObjectSearch.SearchClause;
import iverson.ObjectSearch.SearchClauseType;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import iverson.ObjectSearch.SelectItem;
import iverson.ObjectSearch.WindowFunction;
import iverson.ObjectSearch.WindowFunctionKind;

import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.function.Consumer;

/**
 * Builds one pipeline step (= one CTE): WHERE + (window functions XOR
 * GROUP BY/metrics/HAVING) + derived columns + joins + projection.
 * The input-step parameter is named {@code reads} because {@code from} is a Java keyword.
 */
public final class PipelineStepBuilder {

    private final String name;
    private final List<String> earlierSteps;
    private final PipelineStep.Builder step;

    PipelineStepBuilder(String name, List<String> earlierSteps) {
        this.name = name;
        this.earlierSteps = earlierSteps;
        this.step = PipelineStep.newBuilder().setName(name);
    }

    /** Reads any earlier named step (or "base") instead of the previous step. */
    public PipelineStepBuilder reads(String stepName) {
        boolean known = earlierSteps.stream().anyMatch(s -> s.equalsIgnoreCase(stepName));
        if (!known)
            throw new IllegalArgumentException(
                "Step '" + name + "': reads '" + stepName + "' does not name an earlier step.");
        step.setReads(stepName);
        return this;
    }

    public PipelineStepBuilder where(String field, SearchOperator op, Object value) {
        return addClause(field, op, value, SearchClauseType.FILTER);
    }

    public PipelineStepBuilder not(String field, SearchOperator op, Object value) {
        return addClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    public PipelineStepBuilder withLogic(SearchLogic logic) {
        step.setWhereLogic(logic);
        return this;
    }

    // ── Window functions ─────────────────────────────────────────────────────────

    public PipelineStepBuilder rowNumber(String alias, String orderBy, boolean descending) {
        return rowNumber(alias, orderBy, descending, null);
    }

    public PipelineStepBuilder rowNumber(String alias, String orderBy, boolean descending, String partitionBy) {
        return addWindow(alias, WindowFunctionKind.ROW_NUMBER, "", orderBy, descending, partitionBy, 1);
    }

    public PipelineStepBuilder rank(String alias, String orderBy, boolean descending) {
        return addWindow(alias, WindowFunctionKind.RANK, "", orderBy, descending, null, 1);
    }

    public PipelineStepBuilder denseRank(String alias, String orderBy, boolean descending) {
        return addWindow(alias, WindowFunctionKind.DENSE_RANK, "", orderBy, descending, null, 1);
    }

    public PipelineStepBuilder runningSum(String alias, String field, String orderBy) {
        return addWindow(alias, WindowFunctionKind.RUNNING_SUM, field, orderBy, false, null, 1);
    }

    public PipelineStepBuilder runningAvg(String alias, String field, String orderBy) {
        return addWindow(alias, WindowFunctionKind.RUNNING_AVG, field, orderBy, false, null, 1);
    }

    public PipelineStepBuilder lag(String alias, String field, String orderBy, int offset) {
        return addWindow(alias, WindowFunctionKind.LAG, field, orderBy, false, null, offset);
    }

    public PipelineStepBuilder lead(String alias, String field, String orderBy, int offset) {
        return addWindow(alias, WindowFunctionKind.LEAD, field, orderBy, false, null, offset);
    }

    // ── Aggregation ─────────────────────────────────────────────────────────────

    public PipelineStepBuilder groupBy(String field) {
        return groupBy(field, DateTrunc.NONE);
    }

    public PipelineStepBuilder groupBy(String field, DateTrunc dateTrunc) {
        step.addGroupBy(GroupKey.newBuilder().setField(field).setDateTrunc(dateTrunc).build());
        return this;
    }

    public PipelineStepBuilder sum(String field)                { return addMetric(field + "_sum", AggregationType.SUM, field, null); }
    public PipelineStepBuilder sum(String field, String alias)  { return addMetric(alias, AggregationType.SUM, field, null); }
    public PipelineStepBuilder avg(String field)                { return addMetric(field + "_avg", AggregationType.AVG, field, null); }
    public PipelineStepBuilder avg(String field, String alias)  { return addMetric(alias, AggregationType.AVG, field, null); }
    public PipelineStepBuilder min(String field)                { return addMetric(field + "_min", AggregationType.MIN, field, null); }
    public PipelineStepBuilder min(String field, String alias)  { return addMetric(alias, AggregationType.MIN, field, null); }
    public PipelineStepBuilder max(String field)                { return addMetric(field + "_max", AggregationType.MAX, field, null); }
    public PipelineStepBuilder max(String field, String alias)  { return addMetric(alias, AggregationType.MAX, field, null); }
    public PipelineStepBuilder count(String field)               { return addMetric(field + "_count", AggregationType.COUNT, field, null); }
    public PipelineStepBuilder count(String field, String alias) { return addMetric(alias, AggregationType.COUNT, field, null); }
    public PipelineStepBuilder countAll()                        { return countAll("count"); }
    public PipelineStepBuilder countAll(String alias)            { return addMetric(alias, AggregationType.COUNT, null, null); }
    public PipelineStepBuilder sumExpr(String expression, String alias) { return addMetric(alias, AggregationType.SUM, null, expression); }
    public PipelineStepBuilder avgExpr(String expression, String alias) { return addMetric(alias, AggregationType.AVG, null, expression); }

    public PipelineStepBuilder having(String alias, SearchOperator op, Object value) {
        step.addHaving(SearchClause.newBuilder()
            .setProperty(alias)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.FILTER)
            .build());
        return this;
    }

    // ── Derived columns, joins, projection ──────────────────────────────────────

    public PipelineStepBuilder derive(String alias, String expr) {
        step.addDerive(DeriveColumn.newBuilder().setAlias(alias).setExpr(expr).build());
        return this;
    }

    public PipelineStepBuilder join(String source, String onLeft, String onRight) {
        return join(source, onLeft, onRight, JoinKind.INNER);
    }

    public PipelineStepBuilder join(String source, String onLeft, String onRight, JoinKind kind) {
        step.addJoins(PipelineJoin.newBuilder()
            .setSource(source)
            .setKind(kind)
            .addOn(JoinCondition.newBuilder().setLeft(onLeft).setRight(onRight).build())
            .build());
        return this;
    }

    public PipelineStepBuilder select(Consumer<SelectSpecBuilder> configure) {
        SelectSpecBuilder builder = new SelectSpecBuilder();
        configure.accept(builder);
        step.addAllSelect(builder.items());
        return this;
    }

    // ── Build + validation ──────────────────────────────────────────────────────

    PipelineStep buildStep() {
        PipelineStep built = step.build();
        boolean isAggregate = built.getGroupByCount() > 0
            || built.getMetricsCount() > 0 || built.getHavingCount() > 0;

        if (built.getWindowsCount() > 0 && isAggregate)
            throw new IllegalArgumentException(
                "Step '" + name + "': window functions and GROUP BY/metrics/HAVING cannot share a step.");
        if ((built.getMetricsCount() > 0 || built.getHavingCount() > 0) && built.getGroupByCount() == 0)
            throw new IllegalArgumentException(
                "Step '" + name + "': metrics/HAVING require at least one groupBy key.");
        if (built.getJoinsCount() > 0 && built.getSelectCount() == 0)
            throw new IllegalArgumentException(
                "Step '" + name + "': a step with joins requires a select projection.");

        Set<String> aliases = new HashSet<>();
        for (WindowFunction w : built.getWindowsList()) requireUnique(aliases, w.getAlias());
        for (DeriveColumn d : built.getDeriveList())   requireUnique(aliases, d.getAlias());
        for (MetricSpec m : built.getMetricsList())    requireUnique(aliases, m.getName());
        for (SelectItem s : built.getSelectList())
            if (!s.getAlias().isEmpty()) requireUnique(aliases, s.getAlias());

        return built;
    }

    private void requireUnique(Set<String> seen, String alias) {
        if (!seen.add(alias.toLowerCase(Locale.ROOT)))
            throw new IllegalArgumentException(
                "Step '" + name + "': duplicate output alias '" + alias + "'.");
    }

    private PipelineStepBuilder addClause(
            String field, SearchOperator op, Object value, SearchClauseType clauseType) {
        step.addWhere(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(clauseType)
            .build());
        return this;
    }

    private PipelineStepBuilder addWindow(
            String alias, WindowFunctionKind kind, String field, String orderBy,
            boolean descending, String partitionBy, int offset) {
        step.addWindows(WindowFunction.newBuilder()
            .setAlias(alias)
            .setKind(kind)
            .setField(field)
            .setOrderBy(orderBy)
            .setDescending(descending)
            .setPartitionBy(partitionBy == null ? "" : partitionBy)
            .setOffset(offset)
            .build());
        return this;
    }

    private PipelineStepBuilder addMetric(
            String alias, AggregationType type, String field, String expression) {
        MetricSpec.Builder spec = MetricSpec.newBuilder().setName(alias).setType(type);
        if (field != null) spec.setField(field);
        if (expression != null) spec.setExpression(expression);
        step.addMetrics(spec.build());
        return this;
    }
}
```

Create `SelectSpecBuilder.java`:

```java
package io.iverson.client.search;

import iverson.ObjectSearch.SelectItem;

import java.util.ArrayList;
import java.util.List;

/** Builds a joined step's projection: which columns survive the join. */
public final class SelectSpecBuilder {

    private final List<SelectItem> items = new ArrayList<>();

    /** All columns from a source ("base", a step name, or a joined type name). */
    public SelectSpecBuilder allFrom(String source) {
        items.add(SelectItem.newBuilder().setSource(source).setAll(true).build());
        return this;
    }

    /** One column from a source. */
    public SelectSpecBuilder pick(String source, String column) {
        return pick(source, column, null);
    }

    /** One column from a source, renamed. */
    public SelectSpecBuilder pick(String source, String column, String alias) {
        items.add(SelectItem.newBuilder()
            .setSource(source)
            .setColumn(column)
            .setAlias(alias == null ? "" : alias)
            .build());
        return this;
    }

    List<SelectItem> items() {
        return items;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd Iverson.Clients/Java/client && mvn -q test`
Expected: BUILD SUCCESS, `PipelineBuilderTest` green plus all existing tests.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Java/client/src
git commit -m "feat(java): pipeline CTE builder"
```

---

### Task 4: Python pipeline builder

**Files:**
- Create: `Iverson.Clients/Python/iverson_client/pipeline.py`
- Modify: `Iverson.Clients/Python/iverson_client/__init__.py` (export `pipeline`, `PipelineBuilder`)
- Test: Create `Iverson.Clients/Python/tests/test_pipeline.py`

**Interfaces:**
- Consumes: regenerated `object_search_pb2` (`scripts/generate_protos.sh`), `iverson_client.search._to_search_value`.
- Produces: `pipeline(type_name)` factory returning `PipelineBuilder` (snake_case surface: `where/not_/with_logic/step/sort_on/sort_on_desc/limit/build`; step builder: `reads/where/not_/with_logic/row_number/rank/dense_rank/running_sum/running_avg/lag/lead/group_by/sum/avg/min/max/count/count_all/sum_expr/avg_expr/having/derive/join/select`).

- [ ] **Step 1: Regenerate protos**

Run: `cd Iverson.Clients/Python && bash scripts/generate_protos.sh`
Expected: `iverson_client/generated/object_search_pb2.py` now contains `PipelineRequest`.

- [ ] **Step 2: Write the failing tests**

Create `tests/test_pipeline.py`:

```python
import pytest

from iverson_client import pipeline
from iverson_client.generated import object_search_pb2 as pb


def test_build_full_pipeline_compiles_to_expected_proto():
    request = (
        pipeline("Article")
        .where("IsPublished", pb.EQUALS, True)
        .step("by_author", lambda s: s
              .group_by("AuthorId")
              .count_all("articles")
              .having("articles", pb.GREATER_THAN, 5))
        .step("ranked", lambda s: s
              .row_number("rank", order_by="articles", descending=True))
        .step("named", lambda s: s
              .join("Author", "AuthorId", "Id")
              .select(lambda sel: sel.all_from("ranked").pick("Author", "Name", "author_name")))
        .sort_on_desc("rank")
        .limit(5)
        .build()
    )

    assert request.type_name == "Article"
    assert len(request.base_where) == 1
    assert len(request.steps) == 3

    agg = request.steps[0]
    assert agg.name == "by_author"
    assert agg.group_by[0].field == "AuthorId"
    assert agg.group_by[0].date_trunc == pb.NONE
    assert agg.metrics[0].name == "articles"
    assert agg.having[0].property == "articles"

    win = request.steps[1]
    assert win.windows[0].kind == pb.ROW_NUMBER
    assert win.windows[0].descending is True

    joined = request.steps[2]
    assert joined.joins[0].source == "Author"
    assert joined.joins[0].on[0].left == "AuthorId"
    assert joined.select[0].all is True
    assert joined.select[1].alias == "author_name"

    assert request.limit == 5


def test_reads_carried_and_defaults_to_10000_limit():
    request = (
        pipeline("Article")
        .step("a", lambda s: s.derive("x", "WordCount + 1"))
        .step("b", lambda s: s.reads("base").derive("y", "WordCount + 2"))
        .build()
    )
    assert request.steps[1].reads == "base"
    assert request.limit == 10_000


def test_duplicate_step_name_raises():
    b = pipeline("Article").step("x", lambda s: s.derive("a", "WordCount"))
    with pytest.raises(ValueError, match="X"):
        b.step("X", lambda s: s.derive("b", "WordCount"))


def test_reads_unknown_step_raises():
    with pytest.raises(ValueError, match="nope"):
        pipeline("Article").step("a", lambda s: s.reads("nope"))


def test_window_and_group_by_in_one_step_raises():
    with pytest.raises(ValueError, match="bad"):
        pipeline("Article").step("bad", lambda s: s
                                 .row_number("rn", order_by="Id")
                                 .group_by("AuthorId").count_all("n"))


def test_join_without_select_raises():
    with pytest.raises(ValueError, match="select"):
        pipeline("Article").step("bad", lambda s: s.join("Author", "AuthorId", "Id"))


def test_duplicate_aliases_raise():
    with pytest.raises(ValueError, match="X"):
        pipeline("Article").step("bad", lambda s: s
                                 .row_number("x", order_by="Id")
                                 .derive("X", "WordCount + 1"))


def test_date_trunc_group_key():
    request = (
        pipeline("Article")
        .step("m", lambda s: s.group_by("PublishedAt", pb.MONTH).count_all("n"))
        .build()
    )
    assert request.steps[0].group_by[0].date_trunc == pb.MONTH
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `cd Iverson.Clients/Python && python -m pytest tests/test_pipeline.py -q`
Expected: FAIL — `pipeline` not importable.

- [ ] **Step 4: Implement**

Create `iverson_client/pipeline.py`:

```python
"""
Fluent pipeline (CTE chain) builder that compiles to a ``PipelineRequest`` proto.

Each ``.step(name, fn)`` is exactly one CTE in the generated StarRocks query; steps
read the previous step by default, or any earlier named step via ``reads``. String-
addressed like ``GroupByBuilder``. ``build()`` never needs a live server.

Usage:
    request = (
        pipeline("Article")
        .where("IsPublished", pb.EQUALS, True)
        .step("by_author", lambda s: s.group_by("AuthorId").count_all("articles"))
        .step("ranked", lambda s: s.row_number("rank", order_by="articles", descending=True))
        .sort_on("rank")
        .limit(10)
        .build()
    )
"""
from __future__ import annotations

from typing import Callable, Optional

from iverson_client.generated import object_search_pb2 as _pb
from iverson_client.search import _to_search_value

_BASE_STEP_NAME = "base"


class SelectSpecBuilder:
    """Builds a joined step's projection: which columns survive the join."""

    def __init__(self) -> None:
        self._items: list[_pb.SelectItem] = []

    def all_from(self, source: str) -> "SelectSpecBuilder":
        """All columns from a source ("base", a step name, or a joined type name)."""
        self._items.append(_pb.SelectItem(source=source, all=True))
        return self

    def pick(self, source: str, column: str, alias: Optional[str] = None) -> "SelectSpecBuilder":
        """One column from a source, optionally renamed."""
        self._items.append(_pb.SelectItem(source=source, column=column, alias=alias or ""))
        return self


class PipelineStepBuilder:
    """One pipeline step (= one CTE): WHERE + (windows XOR group_by/metrics/having)
    + derived columns + joins + projection. The input-step parameter is named
    ``reads`` because ``from`` is a Python keyword."""

    def __init__(self, name: str, earlier_steps: list[str]) -> None:
        self._name = name
        self._earlier = earlier_steps
        self._reads = ""
        self._where: list[_pb.SearchClause] = []
        self._where_logic = _pb.AND
        self._windows: list[_pb.WindowFunction] = []
        self._group_by: list[_pb.GroupKey] = []
        self._metrics: list[_pb.MetricSpec] = []
        self._having: list[_pb.SearchClause] = []
        self._derive: list[_pb.DeriveColumn] = []
        self._joins: list[_pb.PipelineJoin] = []
        self._select: list[_pb.SelectItem] = []

    # ── Input selection ──────────────────────────────────────────────────────

    def reads(self, step_name: str) -> "PipelineStepBuilder":
        """Read any earlier named step (or "base") instead of the previous step."""
        if step_name.lower() not in (s.lower() for s in self._earlier):
            raise ValueError(
                f"Step '{self._name}': reads '{step_name}' does not name an earlier step.")
        self._reads = step_name
        return self

    # ── Filtering ────────────────────────────────────────────────────────────

    def where(self, field: str, op: int, value: object) -> "PipelineStepBuilder":
        return self._add_clause(field, op, value, _pb.FILTER)

    def not_(self, field: str, op: int, value: object) -> "PipelineStepBuilder":
        return self._add_clause(field, op, value, _pb.MUST_NOT)

    def with_logic(self, logic: int) -> "PipelineStepBuilder":
        self._where_logic = logic
        return self

    # ── Window functions ─────────────────────────────────────────────────────

    def row_number(self, alias: str, *, order_by: str, descending: bool = False,
                   partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.ROW_NUMBER, "", order_by, descending, partition_by)

    def rank(self, alias: str, *, order_by: str, descending: bool = False,
             partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.RANK, "", order_by, descending, partition_by)

    def dense_rank(self, alias: str, *, order_by: str, descending: bool = False,
                   partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.DENSE_RANK, "", order_by, descending, partition_by)

    def running_sum(self, alias: str, field: str, *, order_by: str,
                    descending: bool = False,
                    partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.RUNNING_SUM, field, order_by, descending, partition_by)

    def running_avg(self, alias: str, field: str, *, order_by: str,
                    descending: bool = False,
                    partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.RUNNING_AVG, field, order_by, descending, partition_by)

    def lag(self, alias: str, field: str, *, order_by: str, offset: int = 1,
            descending: bool = False,
            partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.LAG, field, order_by, descending, partition_by, offset)

    def lead(self, alias: str, field: str, *, order_by: str, offset: int = 1,
             descending: bool = False,
             partition_by: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_window(alias, _pb.LEAD, field, order_by, descending, partition_by, offset)

    # ── Aggregation ──────────────────────────────────────────────────────────

    def group_by(self, field: str, date_trunc: int = _pb.NONE) -> "PipelineStepBuilder":
        self._group_by.append(_pb.GroupKey(field=field, date_trunc=date_trunc))
        return self

    def sum(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_sum", _pb.SUM, field, None)

    def avg(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_avg", _pb.AVG, field, None)

    def min(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_min", _pb.MIN, field, None)

    def max(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_max", _pb.MAX, field, None)

    def count(self, field: str, alias: Optional[str] = None) -> "PipelineStepBuilder":
        return self._add_metric(alias or f"{field}_count", _pb.COUNT, field, None)

    def count_all(self, alias: str = "count") -> "PipelineStepBuilder":
        return self._add_metric(alias, _pb.COUNT, None, None)

    def sum_expr(self, expression: str, alias: str) -> "PipelineStepBuilder":
        return self._add_metric(alias, _pb.SUM, None, expression)

    def avg_expr(self, expression: str, alias: str) -> "PipelineStepBuilder":
        return self._add_metric(alias, _pb.AVG, None, expression)

    def having(self, alias: str, op: int, value: object) -> "PipelineStepBuilder":
        self._having.append(_pb.SearchClause(
            property=alias, operator=op,
            value=_to_search_value(value), clause_type=_pb.FILTER))
        return self

    # ── Derived columns, joins, projection ───────────────────────────────────

    def derive(self, alias: str, expr: str) -> "PipelineStepBuilder":
        self._derive.append(_pb.DeriveColumn(alias=alias, expr=expr))
        return self

    def join(self, source: str, on_left: str, on_right: str,
             kind: int = _pb.JoinKind.INNER) -> "PipelineStepBuilder":
        self._joins.append(_pb.PipelineJoin(
            source=source, kind=kind,
            on=[_pb.JoinCondition(left=on_left, right=on_right)]))
        return self

    def select(self, configure: Callable[[SelectSpecBuilder], object]) -> "PipelineStepBuilder":
        builder = SelectSpecBuilder()
        configure(builder)
        self._select.extend(builder._items)
        return self

    # ── Build + validation ───────────────────────────────────────────────────

    def _build_step(self) -> _pb.PipelineStep:
        is_aggregate = bool(self._group_by or self._metrics or self._having)
        if self._windows and is_aggregate:
            raise ValueError(
                f"Step '{self._name}': window functions and group_by/metrics/having "
                "cannot share a step.")
        if (self._metrics or self._having) and not self._group_by:
            raise ValueError(
                f"Step '{self._name}': metrics/having require at least one group_by key.")
        if self._joins and not self._select:
            raise ValueError(
                f"Step '{self._name}': a step with joins requires a select projection.")

        seen: set[str] = set()
        aliases = ([w.alias for w in self._windows]
                   + [d.alias for d in self._derive]
                   + [m.name for m in self._metrics]
                   + [s.alias for s in self._select if s.alias])
        for a in aliases:
            if a.lower() in seen:
                raise ValueError(f"Step '{self._name}': duplicate output alias '{a}'.")
            seen.add(a.lower())

        return _pb.PipelineStep(
            name=self._name, reads=self._reads,
            where=self._where, where_logic=self._where_logic,
            windows=self._windows, group_by=self._group_by,
            metrics=self._metrics, having=self._having,
            derive=self._derive, joins=self._joins, select=self._select)

    def _add_clause(self, field: str, op: int, value: object,
                    clause_type: int) -> "PipelineStepBuilder":
        self._where.append(_pb.SearchClause(
            property=field, operator=op,
            value=_to_search_value(value), clause_type=clause_type))
        return self

    def _add_window(self, alias: str, kind: int, field: str, order_by: str,
                    descending: bool, partition_by: Optional[str],
                    offset: int = 1) -> "PipelineStepBuilder":
        self._windows.append(_pb.WindowFunction(
            alias=alias, kind=kind, field=field, order_by=order_by,
            descending=descending, partition_by=partition_by or "", offset=offset))
        return self

    def _add_metric(self, alias: str, agg_type: int, field: Optional[str],
                    expression: Optional[str]) -> "PipelineStepBuilder":
        self._metrics.append(_pb.MetricSpec(
            name=alias, type=agg_type, field=field or "", expression=expression or ""))
        return self


class PipelineBuilder:
    """Fluent DSL builder that compiles to a ``PipelineRequest`` proto message.
    Instantiate via the module-level ``pipeline(type_name)`` factory."""

    def __init__(self, type_name: str) -> None:
        self._type_name = type_name
        self._base_where: list[_pb.SearchClause] = []
        self._base_logic = _pb.AND
        self._steps: list[_pb.PipelineStep] = []
        self._order_by: list[_pb.SearchSort] = []
        self._limit = 10_000

    def where(self, field: str, op: int, value: object) -> "PipelineBuilder":
        return self._add_base_clause(field, op, value, _pb.FILTER)

    def not_(self, field: str, op: int, value: object) -> "PipelineBuilder":
        return self._add_base_clause(field, op, value, _pb.MUST_NOT)

    def with_logic(self, logic: int) -> "PipelineBuilder":
        self._base_logic = logic
        return self

    def step(self, name: str,
             configure: Callable[[PipelineStepBuilder], object]) -> "PipelineBuilder":
        if not name:
            raise ValueError("Step name must be non-empty.")
        if name.lower() == _BASE_STEP_NAME:
            raise ValueError(f"Step name '{name}' is reserved for the implicit base step.")
        if name.lower() in (s.name.lower() for s in self._steps):
            raise ValueError(f"Duplicate step name '{name}'.")

        earlier = [_BASE_STEP_NAME] + [s.name for s in self._steps]
        builder = PipelineStepBuilder(name, earlier)
        configure(builder)
        self._steps.append(builder._build_step())
        return self

    def sort_on(self, field: str, descending: bool = False) -> "PipelineBuilder":
        self._order_by.append(_pb.SearchSort(property=field, descending=descending))
        return self

    def sort_on_desc(self, field: str) -> "PipelineBuilder":
        return self.sort_on(field, descending=True)

    def limit(self, n: int) -> "PipelineBuilder":
        self._limit = n
        return self

    def build(self, trace_id: str = "") -> _pb.PipelineRequest:
        return _pb.PipelineRequest(
            type_name=self._type_name,
            base_where=self._base_where,
            base_logic=self._base_logic,
            steps=self._steps,
            order_by=self._order_by,
            limit=self._limit,
            trace_id=trace_id)

    def _add_base_clause(self, field: str, op: int, value: object,
                         clause_type: int) -> "PipelineBuilder":
        self._base_where.append(_pb.SearchClause(
            property=field, operator=op,
            value=_to_search_value(value), clause_type=clause_type))
        return self


def pipeline(type_name: str) -> PipelineBuilder:
    """Start a fluent pipeline (CTE chain) for the given entity type."""
    return PipelineBuilder(type_name)
```

Add to `iverson_client/__init__.py` (next to the existing `GroupByBuilder` import/export lines):

```python
from iverson_client.pipeline import PipelineBuilder, PipelineStepBuilder, pipeline
```

and append `"PipelineBuilder"`, `"PipelineStepBuilder"`, `"pipeline"` to `__all__`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd Iverson.Clients/Python && python -m pytest -q`
Expected: PASS (new + existing tests).

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Python/iverson_client Iverson.Clients/Python/tests/test_pipeline.py
git commit -m "feat(python): pipeline CTE builder"
```

---

### Task 5: TypeScript pipeline builder

**Files:**
- Create: `Iverson.Clients/TypeScript/src/pipeline.ts`
- Modify: `Iverson.Clients/TypeScript/src/index.ts` (export)
- Test: Create `Iverson.Clients/TypeScript/tests/pipeline.test.ts`

**Interfaces:**
- Consumes: regenerated `generated/object_search.ts` (`scripts/generate_protos.sh`), `toSearchValue` from `./search.js`.
- Produces: `pipeline(typeName)` factory returning `PipelineBuilder`; camelCase surface mirroring Task 3's Java names (`where/not/withLogic/step/sortOn/sortOnDesc/limit/build`; step: `reads/where/not/withLogic/rowNumber/rank/denseRank/runningSum/runningAvg/lag/lead/groupBy/sum/avg/min/max/count/countAll/sumExpr/avgExpr/having/derive/join/select`).

- [ ] **Step 1: Regenerate protos**

Run: `cd Iverson.Clients/TypeScript && bash scripts/generate_protos.sh`
Expected: `generated/object_search.ts` now exports `PipelineRequest`, `PipelineStep`, `WindowFunctionKind`, `DateTrunc`, etc.

- [ ] **Step 2: Write the failing tests**

Create `tests/pipeline.test.ts`:

```typescript
import { describe, expect, it } from 'vitest';
import { pipeline } from '../src/pipeline.js';
import {
    AggregationType,
    DateTrunc,
    JoinKind,
    SearchOperator,
    WindowFunctionKind,
} from '../generated/object_search.js';

describe('PipelineBuilder', () => {
    it('compiles a full pipeline to the expected proto', () => {
        const request = pipeline('Article')
            .where('IsPublished', SearchOperator.EQUALS, true)
            .step('by_author', s => s
                .groupBy('AuthorId')
                .countAll('articles')
                .having('articles', SearchOperator.GREATER_THAN, 5))
            .step('ranked', s => s
                .rowNumber('rank', { orderBy: 'articles', descending: true }))
            .step('named', s => s
                .join('Author', 'AuthorId', 'Id')
                .select(sel => sel.allFrom('ranked').pick('Author', 'Name', 'author_name')))
            .sortOnDesc('rank')
            .limit(5)
            .build();

        expect(request.typeName).toBe('Article');
        expect(request.baseWhere).toHaveLength(1);
        expect(request.steps).toHaveLength(3);

        const agg = request.steps[0];
        expect(agg.name).toBe('by_author');
        expect(agg.groupBy[0]).toMatchObject({ field: 'AuthorId', dateTrunc: DateTrunc.NONE });
        expect(agg.metrics[0]).toMatchObject({ name: 'articles', type: AggregationType.COUNT });
        expect(agg.having[0].property).toBe('articles');

        const win = request.steps[1];
        expect(win.windows[0]).toMatchObject({
            alias: 'rank', kind: WindowFunctionKind.ROW_NUMBER,
            orderBy: 'articles', descending: true,
        });

        const joined = request.steps[2];
        expect(joined.joins[0]).toMatchObject({ source: 'Author', kind: JoinKind.INNER });
        expect(joined.joins[0].on[0]).toMatchObject({ left: 'AuthorId', right: 'Id' });
        expect(joined.select[0].all).toBe(true);
        expect(joined.select[1].alias).toBe('author_name');

        expect(request.limit).toBe(5);
    });

    it('carries explicit reads and defaults the limit', () => {
        const request = pipeline('Article')
            .step('a', s => s.derive('x', 'WordCount + 1'))
            .step('b', s => s.reads('base').derive('y', 'WordCount + 2'))
            .build();

        expect(request.steps[1].reads).toBe('base');
        expect(request.limit).toBe(10_000);
    });

    it('throws on duplicate step names', () => {
        const b = pipeline('Article').step('x', s => s.derive('a', 'WordCount'));
        expect(() => b.step('X', s => s.derive('b', 'WordCount'))).toThrow(/X/);
    });

    it('throws on reads of an unknown step', () => {
        expect(() => pipeline('Article').step('a', s => s.reads('nope'))).toThrow(/nope/);
    });

    it('throws when windows and groupBy share a step', () => {
        expect(() => pipeline('Article').step('bad', s => s
            .rowNumber('rn', { orderBy: 'Id' })
            .groupBy('AuthorId').countAll('n'))).toThrow(/bad/);
    });

    it('throws on a join without a select', () => {
        expect(() => pipeline('Article').step('bad', s => s.join('Author', 'AuthorId', 'Id')))
            .toThrow(/select/);
    });

    it('throws on duplicate aliases within a step', () => {
        expect(() => pipeline('Article').step('bad', s => s
            .rowNumber('x', { orderBy: 'Id' })
            .derive('X', 'WordCount + 1'))).toThrow(/X/);
    });
});
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `cd Iverson.Clients/TypeScript && npx vitest run tests/pipeline.test.ts`
Expected: FAIL — module `../src/pipeline.js` not found.

- [ ] **Step 4: Implement**

Create `src/pipeline.ts`:

```typescript
/**
 * Fluent pipeline (CTE chain) builder that compiles to a PipelineRequest proto.
 *
 * Each `.step(name, fn)` is exactly one CTE in the generated StarRocks query; steps
 * read the previous step by default, or any earlier named step via `reads`.
 * String-addressed like GroupByBuilder; `build()` never needs a live server.
 */

import {
    AggregationType,
    DateTrunc,
    DeriveColumn,
    GroupKey,
    JoinKind,
    MetricSpec,
    PipelineJoin,
    PipelineRequest,
    PipelineStep,
    SearchClause,
    SearchClauseType,
    SearchLogic,
    SearchOperator,
    SearchSort,
    SelectItem,
    WindowFunction,
    WindowFunctionKind,
} from '../generated/object_search.js';
import { toSearchValue } from './search.js';

const BASE_STEP_NAME = 'base';

interface WindowOptions {
    orderBy: string;
    descending?: boolean;
    partitionBy?: string;
    offset?: number;
}

/** Builds a joined step's projection: which columns survive the join. */
export class SelectSpecBuilder {
    readonly items: SelectItem[] = [];

    /** All columns from a source ('base', a step name, or a joined type name). */
    allFrom(source: string): this {
        this.items.push({ source, column: '', all: true, alias: '' });
        return this;
    }

    /** One column from a source, optionally renamed. */
    pick(source: string, column: string, alias = ''): this {
        this.items.push({ source, column, all: false, alias });
        return this;
    }
}

/** One pipeline step (= one CTE). */
export class PipelineStepBuilder {
    private readonly _name: string;
    private readonly _earlier: string[];
    private _reads = '';
    private readonly _where: SearchClause[] = [];
    private _whereLogic: SearchLogic = SearchLogic.AND;
    private readonly _windows: WindowFunction[] = [];
    private readonly _groupBy: GroupKey[] = [];
    private readonly _metrics: MetricSpec[] = [];
    private readonly _having: SearchClause[] = [];
    private readonly _derive: DeriveColumn[] = [];
    private readonly _joins: PipelineJoin[] = [];
    private readonly _select: SelectItem[] = [];

    constructor(name: string, earlier: string[]) {
        this._name = name;
        this._earlier = earlier;
    }

    /** Read any earlier named step (or 'base') instead of the previous step. */
    reads(stepName: string): this {
        if (!this._earlier.some(s => s.toLowerCase() === stepName.toLowerCase())) {
            throw new Error(
                `Step '${this._name}': reads '${stepName}' does not name an earlier step.`);
        }
        this._reads = stepName;
        return this;
    }

    where(field: string, op: SearchOperator, value: unknown): this {
        return this._addClause(field, op, value, SearchClauseType.FILTER);
    }

    not(field: string, op: SearchOperator, value: unknown): this {
        return this._addClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    withLogic(logic: SearchLogic): this {
        this._whereLogic = logic;
        return this;
    }

    rowNumber(alias: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.ROW_NUMBER, '', opts);
    }

    rank(alias: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.RANK, '', opts);
    }

    denseRank(alias: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.DENSE_RANK, '', opts);
    }

    runningSum(alias: string, field: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.RUNNING_SUM, field, opts);
    }

    runningAvg(alias: string, field: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.RUNNING_AVG, field, opts);
    }

    lag(alias: string, field: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.LAG, field, opts);
    }

    lead(alias: string, field: string, opts: WindowOptions): this {
        return this._addWindow(alias, WindowFunctionKind.LEAD, field, opts);
    }

    groupBy(field: string, dateTrunc: DateTrunc = DateTrunc.NONE): this {
        this._groupBy.push({ field, dateTrunc });
        return this;
    }

    sum(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_sum`, AggregationType.SUM, field, '');
    }

    avg(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_avg`, AggregationType.AVG, field, '');
    }

    min(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_min`, AggregationType.MIN, field, '');
    }

    max(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_max`, AggregationType.MAX, field, '');
    }

    count(field: string, alias?: string): this {
        return this._addMetric(alias ?? `${field}_count`, AggregationType.COUNT, field, '');
    }

    countAll(alias = 'count'): this {
        return this._addMetric(alias, AggregationType.COUNT, '', '');
    }

    sumExpr(expression: string, alias: string): this {
        return this._addMetric(alias, AggregationType.SUM, '', expression);
    }

    avgExpr(expression: string, alias: string): this {
        return this._addMetric(alias, AggregationType.AVG, '', expression);
    }

    having(alias: string, op: SearchOperator, value: unknown): this {
        this._having.push({
            property: alias, operator: op,
            value: toSearchValue(value), clauseType: SearchClauseType.FILTER,
        });
        return this;
    }

    derive(alias: string, expr: string): this {
        this._derive.push({ alias, expr });
        return this;
    }

    join(source: string, onLeft: string, onRight: string, kind: JoinKind = JoinKind.INNER): this {
        this._joins.push({ source, kind, on: [{ left: onLeft, right: onRight }] });
        return this;
    }

    select(configure: (sel: SelectSpecBuilder) => unknown): this {
        const builder = new SelectSpecBuilder();
        configure(builder);
        this._select.push(...builder.items);
        return this;
    }

    /** @internal */
    buildStep(): PipelineStep {
        const isAggregate =
            this._groupBy.length > 0 || this._metrics.length > 0 || this._having.length > 0;

        if (this._windows.length > 0 && isAggregate) {
            throw new Error(
                `Step '${this._name}': window functions and groupBy/metrics/having cannot share a step.`);
        }
        if ((this._metrics.length > 0 || this._having.length > 0) && this._groupBy.length === 0) {
            throw new Error(
                `Step '${this._name}': metrics/having require at least one groupBy key.`);
        }
        if (this._joins.length > 0 && this._select.length === 0) {
            throw new Error(
                `Step '${this._name}': a step with joins requires a select projection.`);
        }

        const seen = new Set<string>();
        const aliases = [
            ...this._windows.map(w => w.alias),
            ...this._derive.map(d => d.alias),
            ...this._metrics.map(m => m.name),
            ...this._select.filter(s => s.alias !== '').map(s => s.alias),
        ];
        for (const a of aliases) {
            const key = a.toLowerCase();
            if (seen.has(key)) {
                throw new Error(`Step '${this._name}': duplicate output alias '${a}'.`);
            }
            seen.add(key);
        }

        return {
            name: this._name,
            reads: this._reads,
            where: this._where,
            whereLogic: this._whereLogic,
            windows: this._windows,
            groupBy: this._groupBy,
            metrics: this._metrics,
            having: this._having,
            derive: this._derive,
            joins: this._joins,
            select: this._select,
        };
    }

    private _addClause(
        field: string, op: SearchOperator, value: unknown, clauseType: SearchClauseType): this {
        this._where.push({ property: field, operator: op, value: toSearchValue(value), clauseType });
        return this;
    }

    private _addWindow(
        alias: string, kind: WindowFunctionKind, field: string, opts: WindowOptions): this {
        this._windows.push({
            alias, kind, field,
            orderBy: opts.orderBy,
            descending: opts.descending ?? false,
            partitionBy: opts.partitionBy ?? '',
            offset: opts.offset ?? 1,
        });
        return this;
    }

    private _addMetric(
        alias: string, type: AggregationType, field: string, expression: string): this {
        this._metrics.push({ name: alias, type, field, expression });
        return this;
    }
}

/** Fluent DSL builder that compiles to a PipelineRequest proto. */
export class PipelineBuilder {
    private readonly _typeName: string;
    private readonly _baseWhere: SearchClause[] = [];
    private _baseLogic: SearchLogic = SearchLogic.AND;
    private readonly _steps: PipelineStep[] = [];
    private readonly _orderBy: SearchSort[] = [];
    private _limit = 10_000;

    constructor(typeName: string) {
        this._typeName = typeName;
    }

    where(field: string, op: SearchOperator, value: unknown): this {
        return this._addBaseClause(field, op, value, SearchClauseType.FILTER);
    }

    not(field: string, op: SearchOperator, value: unknown): this {
        return this._addBaseClause(field, op, value, SearchClauseType.MUST_NOT);
    }

    withLogic(logic: SearchLogic): this {
        this._baseLogic = logic;
        return this;
    }

    step(name: string, configure: (s: PipelineStepBuilder) => unknown): this {
        if (name === '') throw new Error('Step name must be non-empty.');
        if (name.toLowerCase() === BASE_STEP_NAME) {
            throw new Error(`Step name '${name}' is reserved for the implicit base step.`);
        }
        if (this._steps.some(s => s.name.toLowerCase() === name.toLowerCase())) {
            throw new Error(`Duplicate step name '${name}'.`);
        }

        const earlier = [BASE_STEP_NAME, ...this._steps.map(s => s.name)];
        const builder = new PipelineStepBuilder(name, earlier);
        configure(builder);
        this._steps.push(builder.buildStep());
        return this;
    }

    sortOn(field: string, descending = false): this {
        this._orderBy.push({ property: field, descending });
        return this;
    }

    sortOnDesc(field: string): this {
        return this.sortOn(field, true);
    }

    limit(n: number): this {
        this._limit = n;
        return this;
    }

    build(traceId = ''): PipelineRequest {
        return {
            typeName: this._typeName,
            baseWhere: [...this._baseWhere],
            baseLogic: this._baseLogic,
            steps: [...this._steps],
            orderBy: [...this._orderBy],
            limit: this._limit,
            traceId,
        };
    }

    private _addBaseClause(
        field: string, op: SearchOperator, value: unknown, clauseType: SearchClauseType): this {
        this._baseWhere.push({ property: field, operator: op, value: toSearchValue(value), clauseType });
        return this;
    }
}

/** Start a fluent pipeline (CTE chain) for the given entity type. */
export function pipeline(typeName: string): PipelineBuilder {
    return new PipelineBuilder(typeName);
}
```

Add to `src/index.ts` next to the group-by export:

```typescript
export { PipelineBuilder, PipelineStepBuilder, SelectSpecBuilder, pipeline } from './pipeline.js';
```

If `toSearchValue` is not exported from `src/search.ts`, export it there (the group-by module already imports it the same way — match that import exactly).

- [ ] **Step 5: Run the TypeScript suite**

Run: `cd Iverson.Clients/TypeScript && npx vitest run`
Expected: PASS (new + existing tests).

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/TypeScript/src Iverson.Clients/TypeScript/tests/pipeline.test.ts Iverson.Clients/TypeScript/generated
git commit -m "feat(typescript): pipeline CTE builder"
```

---

### Task 6: Go pipeline builder

**Files:**
- Create: `Iverson.Clients/Go/iverson/pipeline.go`
- Test: Create `Iverson.Clients/Go/iverson_test/pipeline_test.go`

**Interfaces:**
- Consumes: regenerated `pb` package (`scripts/generate_protos.sh`), `toSearchValue` (same package, `search.go:223`).
- Produces: `iverson.NewPipeline(typeName)` returning `*PipelineBuilder`; error accumulation surfaced at `Build()` like `GroupByBuilder`. Go deviations (matching its existing conventions): `Where`/`Having` take `*pb.SearchValue`; window options are explicit args with `""` meaning "no partition"; projection uses flat `SelectAllFrom`/`SelectPick` instead of a lambda sub-builder.

- [ ] **Step 1: Regenerate protos**

Run: `cd Iverson.Clients/Go && bash scripts/generate_protos.sh`
Expected: `generated/` gains `PipelineRequest` etc.; `go build ./...` succeeds.

- [ ] **Step 2: Write the failing tests**

Create `iverson_test/pipeline_test.go`:

```go
package iverson_test

import (
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
)

func numberVal(n float64) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_NumberVal{NumberVal: n}}
}

func boolVal(b bool) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_BoolVal{BoolVal: b}}
}

func TestPipelineBuildFull(t *testing.T) {
	req, err := iverson.NewPipeline("Article").
		Where("IsPublished", pb.SearchOperator_EQUALS, boolVal(true)).
		Step("by_author", func(s *iverson.PipelineStepBuilder) {
			s.GroupBy("AuthorId").
				CountAll("articles").
				Having("articles", pb.SearchOperator_GREATER_THAN, numberVal(5))
		}).
		Step("ranked", func(s *iverson.PipelineStepBuilder) {
			s.RowNumber("rank", "", "articles", true)
		}).
		Step("named", func(s *iverson.PipelineStepBuilder) {
			s.Join("Author", "AuthorId", "Id").
				SelectAllFrom("ranked").
				SelectPick("Author", "Name", "author_name")
		}).
		SortOnDesc("rank").
		Limit(5).
		Build()

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.TypeName != "Article" || len(req.Steps) != 3 || req.Limit != 5 {
		t.Fatalf("unexpected request shape: %+v", req)
	}
	if req.Steps[0].GroupBy[0].Field != "AuthorId" {
		t.Errorf("groupBy field = %q", req.Steps[0].GroupBy[0].Field)
	}
	if req.Steps[1].Windows[0].Kind != pb.WindowFunctionKind_ROW_NUMBER ||
		!req.Steps[1].Windows[0].Descending {
		t.Errorf("window = %+v", req.Steps[1].Windows[0])
	}
	if req.Steps[2].Joins[0].Source != "Author" ||
		req.Steps[2].Joins[0].On[0].Left != "AuthorId" {
		t.Errorf("join = %+v", req.Steps[2].Joins[0])
	}
	if !req.Steps[2].Select[0].All || req.Steps[2].Select[1].Alias != "author_name" {
		t.Errorf("select = %+v", req.Steps[2].Select)
	}
}

func TestPipelineDuplicateStepNameErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("x", func(s *iverson.PipelineStepBuilder) { s.Derive("a", "WordCount") }).
		Step("X", func(s *iverson.PipelineStepBuilder) { s.Derive("b", "WordCount") }).
		Build()
	if err == nil {
		t.Fatal("expected duplicate step name error")
	}
}

func TestPipelineReadsUnknownStepErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("a", func(s *iverson.PipelineStepBuilder) { s.Reads("nope") }).
		Build()
	if err == nil {
		t.Fatal("expected unknown reads error")
	}
}

func TestPipelineWindowAndGroupByErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("bad", func(s *iverson.PipelineStepBuilder) {
			s.RowNumber("rn", "", "Id", false).GroupBy("AuthorId").CountAll("n")
		}).
		Build()
	if err == nil {
		t.Fatal("expected windows-XOR-aggregation error")
	}
}

func TestPipelineJoinWithoutSelectErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("bad", func(s *iverson.PipelineStepBuilder) {
			s.Join("Author", "AuthorId", "Id")
		}).
		Build()
	if err == nil {
		t.Fatal("expected join-requires-select error")
	}
}

func TestPipelineDuplicateAliasErrors(t *testing.T) {
	_, err := iverson.NewPipeline("Article").
		Step("bad", func(s *iverson.PipelineStepBuilder) {
			s.RowNumber("x", "", "Id", false).Derive("X", "WordCount + 1")
		}).
		Build()
	if err == nil {
		t.Fatal("expected duplicate alias error")
	}
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `cd Iverson.Clients/Go && go test ./iverson_test/ -run TestPipeline`
Expected: compile FAILURE (no `NewPipeline`).

- [ ] **Step 4: Implement**

Create `iverson/pipeline.go`:

```go
package iverson

import (
	"fmt"
	"strings"

	pb "github.com/iverson/clients/go/generated"
)

const baseStepName = "base"

// PipelineBuilder builds a PipelineRequest using a fluent API. Each Step is exactly
// one CTE in the generated StarRocks query; steps read the previous step by default,
// or any earlier named step via Reads. Errors accumulate and surface at Build(),
// matching GroupByBuilder.
type PipelineBuilder struct {
	typeName  string
	baseWhere []*pb.SearchClause
	baseLogic pb.SearchLogic
	steps     []*pb.PipelineStep
	orderBy   []*pb.SearchSort
	limit     int32
	err       error
}

// NewPipeline creates a PipelineBuilder for the given entity type name.
func NewPipeline(typeName string) *PipelineBuilder {
	return &PipelineBuilder{
		typeName:  typeName,
		baseLogic: pb.SearchLogic_AND,
		limit:     10_000,
	}
}

// Where adds a WHERE (FILTER) clause on the implicit base step.
func (p *PipelineBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *PipelineBuilder {
	return p.addBaseClause(field, op, val, pb.SearchClauseType_FILTER)
}

// Not adds a MUST_NOT clause on the implicit base step.
func (p *PipelineBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *PipelineBuilder {
	return p.addBaseClause(field, op, val, pb.SearchClauseType_MUST_NOT)
}

// WithLogic sets the logic combining base-step WHERE clauses. Default: AND.
func (p *PipelineBuilder) WithLogic(logic pb.SearchLogic) *PipelineBuilder {
	p.baseLogic = logic
	return p
}

// Step adds one named step (= one CTE).
func (p *PipelineBuilder) Step(name string, configure func(*PipelineStepBuilder)) *PipelineBuilder {
	if name == "" {
		p.fail(fmt.Errorf("step name must be non-empty"))
		return p
	}
	if strings.EqualFold(name, baseStepName) {
		p.fail(fmt.Errorf("step name %q is reserved for the implicit base step", name))
		return p
	}
	for _, s := range p.steps {
		if strings.EqualFold(s.Name, name) {
			p.fail(fmt.Errorf("duplicate step name %q", name))
			return p
		}
	}

	earlier := []string{baseStepName}
	for _, s := range p.steps {
		earlier = append(earlier, s.Name)
	}

	sb := &PipelineStepBuilder{name: name, earlier: earlier,
		step: &pb.PipelineStep{Name: name, WhereLogic: pb.SearchLogic_AND}}
	configure(sb)
	built, err := sb.buildStep()
	if err != nil {
		p.fail(err)
		return p
	}
	p.steps = append(p.steps, built)
	return p
}

// SortOn adds a final ORDER BY on the last step's output.
func (p *PipelineBuilder) SortOn(field string, descending ...bool) *PipelineBuilder {
	desc := false
	if len(descending) > 0 {
		desc = descending[0]
	}
	p.orderBy = append(p.orderBy, &pb.SearchSort{Property: field, Descending: desc})
	return p
}

// SortOnDesc adds a descending final sort.
func (p *PipelineBuilder) SortOnDesc(field string) *PipelineBuilder {
	return p.SortOn(field, true)
}

// Limit sets the final row limit. Default: 10000.
func (p *PipelineBuilder) Limit(n int32) *PipelineBuilder {
	p.limit = n
	return p
}

// Build constructs the PipelineRequest proto. An optional traceId may be supplied.
func (p *PipelineBuilder) Build(traceId ...string) (*pb.PipelineRequest, error) {
	if p.err != nil {
		return nil, p.err
	}
	id := ""
	if len(traceId) > 0 {
		id = traceId[0]
	}
	return &pb.PipelineRequest{
		TypeName:  p.typeName,
		BaseWhere: p.baseWhere,
		BaseLogic: p.baseLogic,
		Steps:     p.steps,
		OrderBy:   p.orderBy,
		Limit:     p.limit,
		TraceId:   id,
	}, nil
}

func (p *PipelineBuilder) addBaseClause(
	field string, op pb.SearchOperator, val *pb.SearchValue, ct pb.SearchClauseType) *PipelineBuilder {
	if val == nil {
		p.fail(fmt.Errorf("field %q: nil search value for operator %v", field, op))
		return p
	}
	p.baseWhere = append(p.baseWhere, &pb.SearchClause{
		Property: field, Operator: op, Value: val, ClauseType: ct,
	})
	return p
}

func (p *PipelineBuilder) fail(err error) {
	if p.err == nil {
		p.err = err
	}
}

// PipelineStepBuilder builds one pipeline step (= one CTE). Go deviations from the
// other clients, matching this package's conventions: Where/Having take raw
// *pb.SearchValue; window partitionBy is an explicit arg where "" means none; the
// projection uses flat SelectAllFrom/SelectPick instead of a lambda sub-builder.
type PipelineStepBuilder struct {
	name    string
	earlier []string
	step    *pb.PipelineStep
	err     error
}

// Reads selects any earlier named step (or "base") instead of the previous step.
func (s *PipelineStepBuilder) Reads(stepName string) *PipelineStepBuilder {
	known := false
	for _, e := range s.earlier {
		if strings.EqualFold(e, stepName) {
			known = true
			break
		}
	}
	if !known {
		s.fail(fmt.Errorf("step %q: reads %q does not name an earlier step", s.name, stepName))
		return s
	}
	s.step.Reads = stepName
	return s
}

// Where adds a WHERE (FILTER) clause against the step's input columns.
func (s *PipelineStepBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *PipelineStepBuilder {
	return s.addClause(field, op, val, pb.SearchClauseType_FILTER)
}

// Not adds a MUST_NOT clause.
func (s *PipelineStepBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *PipelineStepBuilder {
	return s.addClause(field, op, val, pb.SearchClauseType_MUST_NOT)
}

// WithLogic sets the step's WHERE clause logic. Default: AND.
func (s *PipelineStepBuilder) WithLogic(logic pb.SearchLogic) *PipelineStepBuilder {
	s.step.WhereLogic = logic
	return s
}

// RowNumber adds ROW_NUMBER() OVER (...). partitionBy may be "" for none.
func (s *PipelineStepBuilder) RowNumber(alias, partitionBy, orderBy string, descending bool) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_ROW_NUMBER, "", partitionBy, orderBy, descending, 1)
}

// Rank adds RANK() OVER (...).
func (s *PipelineStepBuilder) Rank(alias, partitionBy, orderBy string, descending bool) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_RANK, "", partitionBy, orderBy, descending, 1)
}

// DenseRank adds DENSE_RANK() OVER (...).
func (s *PipelineStepBuilder) DenseRank(alias, partitionBy, orderBy string, descending bool) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_DENSE_RANK, "", partitionBy, orderBy, descending, 1)
}

// RunningSum adds SUM(field) OVER (ORDER BY ...).
func (s *PipelineStepBuilder) RunningSum(alias, field, orderBy string) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_RUNNING_SUM, field, "", orderBy, false, 1)
}

// RunningAvg adds AVG(field) OVER (ORDER BY ...).
func (s *PipelineStepBuilder) RunningAvg(alias, field, orderBy string) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_RUNNING_AVG, field, "", orderBy, false, 1)
}

// Lag adds LAG(field, offset) OVER (ORDER BY ...).
func (s *PipelineStepBuilder) Lag(alias, field, orderBy string, offset int32) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_LAG, field, "", orderBy, false, offset)
}

// Lead adds LEAD(field, offset) OVER (ORDER BY ...).
func (s *PipelineStepBuilder) Lead(alias, field, orderBy string, offset int32) *PipelineStepBuilder {
	return s.addWindow(alias, pb.WindowFunctionKind_LEAD, field, "", orderBy, false, offset)
}

// GroupBy adds a GROUP BY key without date truncation.
func (s *PipelineStepBuilder) GroupBy(field string) *PipelineStepBuilder {
	return s.GroupByTrunc(field, pb.DateTrunc_NONE)
}

// GroupByTrunc adds a GROUP BY key with a DATE_TRUNC interval.
func (s *PipelineStepBuilder) GroupByTrunc(field string, trunc pb.DateTrunc) *PipelineStepBuilder {
	s.step.GroupBy = append(s.step.GroupBy, &pb.GroupKey{Field: field, DateTrunc: trunc})
	return s
}

// Sum adds a SUM metric. Default alias: "{field}_sum".
func (s *PipelineStepBuilder) Sum(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_SUM, field, "", resolveAlias(alias, field, "sum"))
}

// Avg adds an AVG metric. Default alias: "{field}_avg".
func (s *PipelineStepBuilder) Avg(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_AVG, field, "", resolveAlias(alias, field, "avg"))
}

// Min adds a MIN metric. Default alias: "{field}_min".
func (s *PipelineStepBuilder) Min(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_MIN, field, "", resolveAlias(alias, field, "min"))
}

// Max adds a MAX metric. Default alias: "{field}_max".
func (s *PipelineStepBuilder) Max(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_MAX, field, "", resolveAlias(alias, field, "max"))
}

// Count adds a COUNT metric on a field. Default alias: "{field}_count".
func (s *PipelineStepBuilder) Count(field string, alias ...string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_COUNT, field, "", resolveAlias(alias, field, "count"))
}

// CountAll adds COUNT(*). Default alias: "count".
func (s *PipelineStepBuilder) CountAll(alias ...string) *PipelineStepBuilder {
	name := "count"
	if len(alias) > 0 {
		name = alias[0]
	}
	return s.addMetric(pb.AggregationType_COUNT, "", "", name)
}

// SumExpr adds SUM over a raw SQL expression.
func (s *PipelineStepBuilder) SumExpr(expression, alias string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_SUM, "", expression, alias)
}

// AvgExpr adds AVG over a raw SQL expression.
func (s *PipelineStepBuilder) AvgExpr(expression, alias string) *PipelineStepBuilder {
	return s.addMetric(pb.AggregationType_AVG, "", expression, alias)
}

// Having adds a HAVING clause; alias must match a metric alias in this step.
func (s *PipelineStepBuilder) Having(alias string, op pb.SearchOperator, val *pb.SearchValue) *PipelineStepBuilder {
	if val == nil {
		s.fail(fmt.Errorf("step %q: having %q: nil search value", s.name, alias))
		return s
	}
	s.step.Having = append(s.step.Having, &pb.SearchClause{
		Property: alias, Operator: op, Value: val, ClauseType: pb.SearchClauseType_FILTER,
	})
	return s
}

// Derive adds a validated scalar expression column.
func (s *PipelineStepBuilder) Derive(alias, expr string) *PipelineStepBuilder {
	s.step.Derive = append(s.step.Derive, &pb.DeriveColumn{Alias: alias, Expr: expr})
	return s
}

// Join joins the step's input against an earlier step's CTE or a registered entity type.
// The join kind defaults to INNER; pass an explicit pb.JoinKind to override.
func (s *PipelineStepBuilder) Join(source, onLeft, onRight string, opts ...pb.JoinKind) *PipelineStepBuilder {
	kind := pb.JoinKind_INNER
	if len(opts) > 0 {
		kind = opts[0]
	}
	s.step.Joins = append(s.step.Joins, &pb.PipelineJoin{
		Source: source, Kind: kind,
		On: []*pb.JoinCondition{{Left: onLeft, Right: onRight}},
	})
	return s
}

// SelectAllFrom projects all columns from a source ("base", a step name, or a joined type).
func (s *PipelineStepBuilder) SelectAllFrom(source string) *PipelineStepBuilder {
	s.step.Select = append(s.step.Select, &pb.SelectItem{Source: source, All: true})
	return s
}

// SelectPick projects one column from a source, optionally renamed.
func (s *PipelineStepBuilder) SelectPick(source, column string, alias ...string) *PipelineStepBuilder {
	a := ""
	if len(alias) > 0 {
		a = alias[0]
	}
	s.step.Select = append(s.step.Select, &pb.SelectItem{Source: source, Column: column, Alias: a})
	return s
}

func (s *PipelineStepBuilder) buildStep() (*pb.PipelineStep, error) {
	if s.err != nil {
		return nil, s.err
	}
	isAggregate := len(s.step.GroupBy) > 0 || len(s.step.Metrics) > 0 || len(s.step.Having) > 0

	if len(s.step.Windows) > 0 && isAggregate {
		return nil, fmt.Errorf(
			"step %q: window functions and GroupBy/metrics/Having cannot share a step", s.name)
	}
	if (len(s.step.Metrics) > 0 || len(s.step.Having) > 0) && len(s.step.GroupBy) == 0 {
		return nil, fmt.Errorf("step %q: metrics/Having require at least one GroupBy key", s.name)
	}
	if len(s.step.Joins) > 0 && len(s.step.Select) == 0 {
		return nil, fmt.Errorf("step %q: a step with joins requires a select projection", s.name)
	}

	seen := map[string]bool{}
	var aliases []string
	for _, w := range s.step.Windows {
		aliases = append(aliases, w.Alias)
	}
	for _, d := range s.step.Derive {
		aliases = append(aliases, d.Alias)
	}
	for _, m := range s.step.Metrics {
		aliases = append(aliases, m.Name)
	}
	for _, sel := range s.step.Select {
		if sel.Alias != "" {
			aliases = append(aliases, sel.Alias)
		}
	}
	for _, a := range aliases {
		key := strings.ToLower(a)
		if seen[key] {
			return nil, fmt.Errorf("step %q: duplicate output alias %q", s.name, a)
		}
		seen[key] = true
	}

	return s.step, nil
}

func (s *PipelineStepBuilder) addClause(
	field string, op pb.SearchOperator, val *pb.SearchValue, ct pb.SearchClauseType) *PipelineStepBuilder {
	if val == nil {
		s.fail(fmt.Errorf("step %q: field %q: nil search value", s.name, field))
		return s
	}
	s.step.Where = append(s.step.Where, &pb.SearchClause{
		Property: field, Operator: op, Value: val, ClauseType: ct,
	})
	return s
}

func (s *PipelineStepBuilder) addWindow(
	alias string, kind pb.WindowFunctionKind, field, partitionBy, orderBy string,
	descending bool, offset int32) *PipelineStepBuilder {
	s.step.Windows = append(s.step.Windows, &pb.WindowFunction{
		Alias: alias, Kind: kind, Field: field,
		PartitionBy: partitionBy, OrderBy: orderBy,
		Descending: descending, Offset: offset,
	})
	return s
}

func (s *PipelineStepBuilder) addMetric(
	aggType pb.AggregationType, field, expression, alias string) *PipelineStepBuilder {
	s.step.Metrics = append(s.step.Metrics, &pb.MetricSpec{
		Name: alias, Type: aggType, Field: field, Expression: expression,
	})
	return s
}

func (s *PipelineStepBuilder) fail(err error) {
	if s.err == nil {
		s.err = err
	}
}
```

- [ ] **Step 5: Run the Go suite**

Run: `cd Iverson.Clients/Go && go build ./... && go test ./...`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Iverson.Clients/Go/iverson/pipeline.go Iverson.Clients/Go/iverson_test/pipeline_test.go Iverson.Clients/Go/generated
git commit -m "feat(go): pipeline CTE builder"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/one-query-five-languages.md` — add a "Pipelines: CTE chains" section after the "Aggregate: bucketed facets" section, add a capability-matrix row, and update the entry-point table.
- Modify: `docs/pipeline-aggregations-proposal.md` — replace the `**Status:** Proposal` line.

**Interfaces:** Consumes the exact method names produced by Tasks 1 and 3–6 — verify each snippet against the merged builders before writing.

- [ ] **Step 1: Mark the old proposal superseded**

In `docs/pipeline-aggregations-proposal.md` change the status line to:

```markdown
**Status:** Superseded by `docs/superpowers/specs/2026-07-05-pipeline-cte-dsl-design.md` (implemented)
```

- [ ] **Step 2: Add the entry-point row and capability row**

In `docs/one-query-five-languages.md`, add to the "Choosing your entry point" table:

```markdown
| Multi-step analytics: top-N per group, running totals, ratios | `Pipeline` | StarRocks | stream of result rows |
```

Add to the capability matrix:

```markdown
| `PipelineBuilder` (CTE chains) | ✅ | ✅ | ✅ | ✅ | ✅ |
```

- [ ] **Step 3: Add the pipeline section**

Insert after the "Aggregate: bucketed facets" section:

````markdown
---

## Pipelines: CTE chains

Where `GroupBy` is one SELECT, `Pipeline` is a *chain* of them — each named step is exactly
one CTE in a single StarRocks query. Steps read the previous step by default, any earlier
step via `reads`, and can JOIN earlier steps' outputs or other registered types. This is the
tool for top-N per group, running totals, derived ratios, and filter-then-aggregate — one
round trip, no client-side stitching.

*Authors with more than 5 published articles, ranked by article count, with names attached:*

### C#

```csharp
var pipeline = Pipeline.For("Article")
    .Where("IsPublished", EqualTo, true)                    // base CTE
    .Step("by_author", s => s
        .GroupBy("AuthorId")
        .CountAll("articles")
        .Having("articles", GreaterThan, 5))
    .Step("ranked", s => s
        .RowNumber("rank", orderBy: "articles", descending: true))
    .Step("named", s => s
        .Join("Author", ("AuthorId", "Id"))
        .Select(p => p.AllFrom("ranked").Pick("Author", "Name", "author_name")))
    .SortOn("rank")
    .Limit(10);

await foreach (var row in articles.PipelineAsync(pipeline))   // EntityCoordinator<Article>
    Console.WriteLine($"{row["author_name"]}: {row["articles"]}");
```

### Java

```java
var request = Query.pipeline("Article")
    .where("IsPublished", SearchOperator.EQUALS, true)
    .step("by_author", s -> s
        .groupBy("AuthorId")
        .countAll("articles")
        .having("articles", SearchOperator.GREATER_THAN, 5))
    .step("ranked", s -> s.rowNumber("rank", "articles", true))
    .step("named", s -> s
        .join("Author", "AuthorId", "Id")
        .select(sel -> sel.allFrom("ranked").pick("Author", "Name", "author_name")))
    .sortOnDesc("rank")
    .limit(10)
    .build();

var search = ObjectSearchServiceGrpc.newBlockingStub(channel);
search.pipeline(request).forEachRemaining(row -> System.out.println(row.getData()));
```

### Python

```python
request = (
    pipeline("Article")
    .where("IsPublished", pb.EQUALS, True)
    .step("by_author", lambda s: s
          .group_by("AuthorId")
          .count_all("articles")
          .having("articles", pb.GREATER_THAN, 5))
    .step("ranked", lambda s: s.row_number("rank", order_by="articles", descending=True))
    .step("named", lambda s: s
          .join("Author", "AuthorId", "Id")
          .select(lambda sel: sel.all_from("ranked").pick("Author", "Name", "author_name")))
    .sort_on_desc("rank")
    .limit(10)
    .build()
)

for row in search.Pipeline(request):
    print(row.data)
```

### TypeScript

```typescript
const request = pipeline('Article')
    .where('IsPublished', SearchOperator.EQUALS, true)
    .step('by_author', s => s
        .groupBy('AuthorId')
        .countAll('articles')
        .having('articles', SearchOperator.GREATER_THAN, 5))
    .step('ranked', s => s.rowNumber('rank', { orderBy: 'articles', descending: true }))
    .step('named', s => s
        .join('Author', 'AuthorId', 'Id')
        .select(sel => sel.allFrom('ranked').pick('Author', 'Name', 'author_name')))
    .sortOnDesc('rank')
    .limit(10)
    .build();

for await (const row of search.pipeline(request)) {
    console.log(row.data);
}
```

### Go

```go
req, err := iverson.NewPipeline("Article").
    Where("IsPublished", pb.SearchOperator_EQUALS,
        &pb.SearchValue{Kind: &pb.SearchValue_BoolVal{BoolVal: true}}).
    Step("by_author", func(s *iverson.PipelineStepBuilder) {
        s.GroupBy("AuthorId").
            CountAll("articles").
            Having("articles", pb.SearchOperator_GREATER_THAN,
                &pb.SearchValue{Kind: &pb.SearchValue_NumberVal{NumberVal: 5}})
    }).
    Step("ranked", func(s *iverson.PipelineStepBuilder) {
        s.RowNumber("rank", "", "articles", true)
    }).
    Step("named", func(s *iverson.PipelineStepBuilder) {
        s.Join("Author", "AuthorId", "Id").
            SelectAllFrom("ranked").
            SelectPick("Author", "Name", "author_name")
    }).
    SortOnDesc("rank").
    Limit(10).
    Build()

stream, err := client.SearchStub.Pipeline(ctx, req)
```

### The step toolbox

| Call | SQL | Notes |
|------|-----|-------|
| `where` / `not` | `WHERE` in this CTE | May reference prior-step aliases |
| `rowNumber` / `rank` / `denseRank` | `ROW_NUMBER()/RANK()/DENSE_RANK() OVER (...)` | XOR with `groupBy` in the same step |
| `runningSum` / `runningAvg` | `SUM/AVG(f) OVER (ORDER BY ...)` | |
| `lag` / `lead` | `LAG/LEAD(f, offset) OVER (...)` | offset defaults to 1 |
| `groupBy(field, dateTrunc?)` | `GROUP BY` (+ `DATE_TRUNC`) | truncated keys output as `{field}_{interval}` |
| `sum/avg/min/max/count/countAll/sumExpr/avgExpr` | aggregate metrics | same shapes as GroupBy |
| `having` | `HAVING` | references this step's metric aliases |
| `derive(alias, expr)` | scalar expression column | validated: no subqueries, quotes, or semicolons |
| `reads(step)` | `FROM step` | default is the previous step |
| `join(source, left, right, kind?)` | `JOIN ... ON` | source = earlier step OR registered type; joined steps **require** a `select` |
| `select` → `allFrom(src)` / `pick(src, col, alias?)` | projection | resolves join column collisions |

Pipeline gotchas:

- **One step = one CTE.** Filter + aggregate + having fit in a single step; a window over
  the aggregate's output is the *next* step.
- **No per-step LIMIT** — top-N inside the chain is `rowNumber` + a `where` on the alias.
  The request-level `limit` (default 10,000) applies to the final SELECT.
- **Validation is two-layered:** builders reject structural mistakes offline (duplicate
  step names, forward `reads`, windows+groupBy in one step, joins without `select`);
  the server validates every column/alias reference against tracked per-step column sets
  and rejects with `INVALID_ARGUMENT` — never a raw SQL error.
````

- [ ] **Step 4: Verify snippets against the implemented builders, then commit**

Cross-check each language snippet's method names against the merged Task 1/3–6 code (signatures, casing, argument order).

```bash
git add docs/one-query-five-languages.md docs/pipeline-aggregations-proposal.md
git commit -m "docs: pipeline CTE section in one-query-five-languages"
```
