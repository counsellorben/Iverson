# Fluent DSL Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the correctness gaps found in the existing fluent DSL review: silent-drop builds become build-time errors, `GroupByBuilder` gains `Not` and `WithHavingLogic` in all five languages, C# gains the `EndsWith` operator alias and multi-hop joins, and dead `VectorSimilar` clauses fail loudly server-side and disappear from all client builders.

**Architecture:** Pure additive/corrective changes to existing builder classes and one server chokepoint (`StarRocksQueryBuilder.BuildWhere`/`BuildHaving`). No proto changes — `VECTOR_SIMILAR` stays in the enum for wire compatibility; the server rejects it and builders stop emitting it (breaking client-API cleanup, same precedent as the must/should removal).

**Tech Stack:** C# (xUnit/FluentAssertions), Java (JUnit), Python (pytest), TypeScript (vitest), Go (go test), server xUnit.

**Spec:** `docs/superpowers/specs/2026-07-05-pipeline-cte-dsl-design.md` — "Fluent DSL improvements" section.
**Ordering note:** Independent of the pipeline-clients plan. The server task touches the same `BuildWhere`/`BuildHaving` methods the pipeline-server plan refactors in its Task 2 — the change here (replace one `continue` with a throw) applies identically before or after that refactor; if the refactor has landed, the `continue` lives in the resolver-core `BuildWhere` overload instead of the schema overload. Same line content either way.

## Global Constraints

**Model assignment (overrides subagent-driven-development's default per-task judgment):** use
**Opus** for the pre-flight plan review, every per-task reviewer subagent, and the final
whole-branch code reviewer subagent. Use **Sonnet** for every implementer subagent (every
task's Steps 1–5) regardless of that task's apparent complexity. Always pass the model
explicitly when dispatching — never let it inherit the session default.

- Build-time validation errors: C# `InvalidOperationException`, Java `IllegalStateException`, Python `ValueError`, TypeScript `Error`, Go error returned from `Build()` — thrown at `build()`/`Build()` time, not at method-call time (the offending state is only knowable once building).
- `GroupByBuilder` validation set (identical in all five languages, applied in `build()`):
  1. duplicate metric aliases (case-insensitive) rejected
  2. every `having` property must be a declared metric alias or a declared key (case-insensitive exact match)
  3. every `orderBy` property must be a declared metric alias or a declared key
- `VectorSimilar` handling: server throws gRPC `InvalidArgument` with message containing "VECTOR_SIMILAR" and "SearchSimilar"; client builders delete their vector-clause methods AND the tests that used them.
- Run suites: server `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`; C# clients `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj`; Java `cd Iverson.Clients/Java/client && mvn -q test`; Python `cd Iverson.Clients/Python && python -m pytest -q`; TS `cd Iverson.Clients/TypeScript && npx vitest run`; Go `cd Iverson.Clients/Go && go test ./...`.

---

### Task 1: Server — reject VectorSimilar clauses loudly

**Files:**
- Modify: `Iverson.Server/Iverson.Api/StarRocks/StarRocksQueryBuilder.cs` (the `continue` on `SearchOperator.VectorSimilar` in `BuildWhere` — line ~292 pre-refactor, inside the resolver-core overload post-refactor — and the identical line in `BuildHaving`, line ~365)
- Test: `Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs` (append)

**Interfaces:**
- Produces: any `SearchClause` with `Operator == SearchOperator.VectorSimilar` reaching `BuildWhere` or `BuildHaving` now throws `RpcException(InvalidArgument)`. `Search`, `GroupBy`, and `Aggregate` RPCs all route through these methods, so all three surfaces reject it with no per-RPC changes.

- [ ] **Step 1: Write the failing tests**

Append to `StarRocksQueryBuilderTests.cs`:

```csharp
    // ── VectorSimilar rejection ────────────────────────────────────────────────

    private static SearchClause VectorClause() => new()
    {
        Property   = "Name",
        Operator   = SearchOperator.VectorSimilar,
        Value      = new SearchValue { FloatList = new RepeatedFloat { Values = { 0.1f, 0.2f } } },
        ClauseType = SearchClauseType.Filter
    };

    [Fact]
    public void BuildWhere_VectorSimilarClause_ThrowsInvalidArgument()
    {
        var param = new DynamicParameters();
        var act = () => StarRocksQueryBuilder.BuildWhere(
            AuthorSchema(), [VectorClause()], SearchLogic.And, param, out _);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains("VECTOR_SIMILAR")
                     && e.Status.Detail.Contains("SearchSimilar"));
    }

    [Fact]
    public void BuildHaving_VectorSimilarClause_ThrowsInvalidArgument()
    {
        var param = new DynamicParameters();
        var act = () => StarRocksQueryBuilder.BuildHaving(
            [VectorClause()], SearchLogic.And, param);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~VectorSimilarClause"`
Expected: FAIL — no exception thrown (clauses are silently skipped).

- [ ] **Step 3: Implement**

In `BuildWhere` (whichever overload contains the operator switch), replace:

```csharp
            if (clause.Operator == SearchOperator.VectorSimilar) continue;
```

with:

```csharp
            if (clause.Operator == SearchOperator.VectorSimilar)
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "VECTOR_SIMILAR clauses are not supported by the SQL search path; " +
                    "use the SearchSimilar or SearchChunks RPCs for vector search."));
```

Apply the identical replacement in `BuildHaving`.

- [ ] **Step 4: Run the full non-integration server suite**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName!~Integration"`
Expected: PASS. If any existing test constructed a VectorSimilar clause expecting silent skipping, update that test to assert the new `RpcException` instead (search the test file for `VectorSimilar`).

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.Api.Tests/StarRocks/StarRocksQueryBuilderTests.cs
git commit -m "feat(server): reject VECTOR_SIMILAR clauses on SQL search paths"
```

---

### Task 2: C# — EndsWith alias, build validation, multi-hop join, vector removal, GroupByBuilder additions

**Files:**
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/SearchOperators.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/QueryBuilder.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Search/GroupByBuilder.cs`
- Test: `Iverson.Clients/DotNet/Iverson.Client.Search.Tests/QueryBuilderTests.cs`, `Iverson.Clients/DotNet/Iverson.Client.Search.Tests/GroupByBuilderTests.cs`

**Interfaces:**
- Produces:
  - `SearchOperators.EndsWith` static field
  - `QueryBuilder<T>.Build()` throws `InvalidOperationException` when aggregations were configured; `BuildAggregate()` throws when sorting/paging was configured
  - `QueryBuilder<T>.Join<TLeft, TRight>(Expression<Func<TLeft, object>>, Expression<Func<TRight, object>>, JoinKind)` multi-hop overload
  - `WhereVectorSimilar`/`NotVectorSimilar`/`AddVectorClause` removed
  - `GroupByBuilder.Not(string, SearchOperator, object)`, `GroupByBuilder.WithHavingLogic(SearchLogic)`, and the Global Constraints validation set in `Build()`

- [ ] **Step 1: Write the failing tests**

Append to `QueryBuilderTests.cs`:

```csharp
    [Fact]
    public void EndsWith_Alias_MapsToEndsWithOperator()
    {
        SearchOperators.EndsWith.Should().Be(SearchOperator.EndsWith);
    }

    [Fact]
    public void Build_WithAggregationsConfigured_Throws()
    {
        var builder = Query.For<TestArticle>().Avg(a => a.WordCount);
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*BuildAggregate*");
    }

    [Fact]
    public void BuildAggregate_WithSortConfigured_Throws()
    {
        var builder = Query.For<TestArticle>()
            .Avg(a => a.WordCount)
            .OrderBy(a => a.Title);
        var act = () => builder.BuildAggregate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*sort*");
    }

    [Fact]
    public void BuildAggregate_WithPagingConfigured_Throws()
    {
        var builder = Query.For<TestArticle>()
            .Avg(a => a.WordCount)
            .Page(1, 10);
        var act = () => builder.BuildAggregate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*pag*");
    }

    [Fact]
    public void Join_MultiHop_SetsExplicitLeftType()
    {
        var request = Query.For<TestArticle>()
            .Join<TestArticle, TestAuthor>(a => a.AuthorId, au => au.Id)
            .Join<TestAuthor, TestPublisher>(au => au.PublisherId, p => p.Id)
            .Build();

        request.Joins.Should().HaveCount(2);
        request.Joins[1].LeftType.Should().Be("TestAuthor");
        request.Joins[1].RightType.Should().Be("TestPublisher");
    }
```

Use the entity classes already defined in `QueryBuilderTests.cs` for its existing tests; if it has no `TestPublisher`, add alongside its existing fixtures:

```csharp
public class TestPublisher
{
    public string Id { get; set; } = "";
}
```

and ensure the existing `TestAuthor`-equivalent fixture has a `PublisherId` string property (add it if missing — additive, breaks nothing). Match the file's actual fixture class names — if they are `Article`/`Author` rather than `TestArticle`/`TestAuthor`, use those names in the tests above.

Also DELETE any tests in `QueryBuilderTests.cs` that call `WhereVectorSimilar`/`NotVectorSimilar` (grep the file for `VectorSimilar`).

Append to `GroupByBuilderTests.cs`:

```csharp
    [Fact]
    public void Not_AddsMustNotClause()
    {
        var request = Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .Not("Category", SearchOperator.Equals, "spam")
            .Build();

        request.Query.Clauses.Should().ContainSingle(c =>
            c.ClauseType == SearchClauseType.MustNot && c.Property == "Category");
    }

    [Fact]
    public void WithHavingLogic_Or_IsCarried()
    {
        var request = Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .Having("n", SearchOperator.GreaterThan, 5)
            .Having("n", SearchOperator.LessThan, 2)
            .WithHavingLogic(SearchLogic.Or)
            .Build();

        request.Having.Logic.Should().Be(SearchLogic.Or);
    }

    [Fact]
    public void Build_DuplicateMetricAliases_Throws()
    {
        var builder = Query.GroupBy("Article")
            .Keys("Category")
            .Sum("WordCount")
            .Sum("WordCount");
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*WordCount_sum*");
    }

    [Fact]
    public void Build_HavingUnknownAlias_Throws()
    {
        var builder = Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .Having("misspelled", SearchOperator.GreaterThan, 5);
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*misspelled*");
    }

    [Fact]
    public void Build_HavingOnKey_IsAllowed()
    {
        var act = () => Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .Having("Category", SearchOperator.Equals, "tech")
            .Build();
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_OrderByUnknownAlias_Throws()
    {
        var builder = Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .OrderBy("nope");
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*nope*");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj`
Expected: new tests FAIL (missing members / no exceptions); vector-similar test deletions keep the rest green.

- [ ] **Step 3: Implement**

`SearchOperators.cs` — replace the `VectorSimilar` line with `EndsWith`:

```csharp
    public static readonly SearchOperator EndsWith             = SearchOperator.EndsWith;
```

(The `VectorSimilar` alias is removed together with the builder methods below.)

`QueryBuilder.cs`:
1. Delete `WhereVectorSimilar`, `NotVectorSimilar` (lines ~43-49) and `AddVectorClause` (lines ~219-232).
2. Add a paging flag and multi-hop join:

```csharp
    private bool _pagingSet;

    public QueryBuilder<T> Page(int page, int size = 20)
    {
        _page      = page;
        _pageSize  = size;
        _pagingSet = true;
        return this;
    }

    /// <summary>
    /// Joins from an already-joined type to another registered type — the multi-hop form.
    /// The single-type <see cref="Join{TRight}"/> always joins from <typeparamref name="T"/>.
    /// </summary>
    public QueryBuilder<T> Join<TLeft, TRight>(
        Expression<Func<TLeft, object>> leftField,
        Expression<Func<TRight, object>> rightField,
        JoinKind kind = JoinKind.Inner)
        where TLeft : class
        where TRight : class
    {
        _joins.Add(new JoinSpec
        {
            LeftType   = typeof(TLeft).Name,
            RightType  = typeof(TRight).Name,
            LeftField  = PropertyNameObj(leftField),
            RightField = PropertyNameObj(rightField),
            Kind       = kind
        });
        return this;
    }
```

(Delete the old `Page` body it replaces.)

3. Guard the two build methods (at the top of each):

```csharp
    public SearchRequest Build()
    {
        if (_aggregations.Count > 0)
            throw new InvalidOperationException(
                "Aggregations were configured but Build() produces a SearchRequest, which " +
                "ignores them — call BuildAggregate() instead.");
        // ... existing body unchanged ...
    }

    public AggregateRequest BuildAggregate(string? traceId = null)
    {
        if (_sorts.Count > 0)
            throw new InvalidOperationException(
                "OrderBy has no effect on aggregations — sort/bucket order is determined by " +
                "the aggregation type. Remove the OrderBy call.");
        if (_pagingSet)
            throw new InvalidOperationException(
                "Page has no effect on aggregations — paging applies to Search only. " +
                "Remove the Page call.");
        // ... existing body unchanged ...
    }
```

`GroupByBuilder.cs`:
1. Add a having-logic field and the two new methods next to `Where`/`Having`:

```csharp
    private SearchLogic _havingLogic = SearchLogic.And;

    /// <summary>Adds a MUST_NOT WHERE clause (excludes matches before grouping).</summary>
    public GroupByBuilder Not(string field, SearchOperator op, object value)
        => AddWhere(field, op, value, SearchClauseType.MustNot);

    /// <summary>Sets the logic combining HAVING clauses. Defaults to AND.</summary>
    public GroupByBuilder WithHavingLogic(SearchLogic logic)
    {
        _havingLogic = logic;
        return this;
    }
```

2. In `Build`, replace `Having = new SearchQuery { Logic = SearchLogic.And }` with `Having = new SearchQuery { Logic = _havingLogic }`, and add validation at the top:

```csharp
    public GroupByRequest Build(string? traceId = null)
    {
        var aliasSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _metrics)
            if (!aliasSet.Add(m.Name))
                throw new InvalidOperationException($"Duplicate metric alias '{m.Name}'.");
        foreach (var k in _keys) aliasSet.Add(k);

        foreach (var h in _having)
            if (!aliasSet.Contains(h.Property))
                throw new InvalidOperationException(
                    $"HAVING references '{h.Property}', which is neither a metric alias nor a key.");
        foreach (var s in _orderBy)
            if (!aliasSet.Contains(s.Property))
                throw new InvalidOperationException(
                    $"OrderBy references '{s.Property}', which is neither a metric alias nor a key.");

        // ... existing body unchanged ...
    }
```

- [ ] **Step 4: Run the C# client tests**

Run: `dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj && dotnet build Iverson.Clients/DotNet/Iverson.Client.slnx`
Expected: PASS. If `Iverson.Client.Sample` used `WhereVectorSimilar`, replace that usage with a `SearchSimilar` call or delete the snippet (build must stay green).

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/DotNet
git commit -m "feat(dotnet): DSL correctness - EndsWith, build validation, multi-hop joins, GroupBy Not/HavingLogic, drop vector clauses"
```

---

### Task 3: Java — GroupByBuilder additions, build validation, vector removal

**Files:**
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/GroupByBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/QueryBuilder.java` (delete `vectorSimilar` at line ~234 and any helper only it uses)
- Test: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/GroupByBuilderTest.java`, `.../QueryBuilderTest.java`

**Interfaces:**
- Produces: `GroupByBuilder.not(String, SearchOperator, Object)`, `GroupByBuilder.withHavingLogic(SearchLogic)`, validation in `build()` throwing `IllegalStateException`; `QueryBuilder` condition chain no longer offers `vectorSimilar`.

- [ ] **Step 1: Write the failing tests**

Append to `GroupByBuilderTest.java`:

```java
    @Test
    void notAddsMustNotClause() {
        var req = Query.groupBy("Article")
            .keys("Category")
            .countAll("n")
            .not("Category", SearchOperator.EQUALS, "spam")
            .build();

        assertEquals(SearchClauseType.MUST_NOT, req.getQuery().getClauses(0).getClauseType());
    }

    @Test
    void withHavingLogicOrIsCarried() {
        var req = Query.groupBy("Article")
            .keys("Category")
            .countAll("n")
            .having("n", SearchOperator.GREATER_THAN, 5)
            .withHavingLogic(SearchLogic.OR)
            .build();

        assertEquals(SearchLogic.OR, req.getHaving().getLogic());
    }

    @Test
    void duplicateMetricAliasThrows() {
        var b = Query.groupBy("Article").keys("Category").sum("WordCount").sum("WordCount");
        assertThrows(IllegalStateException.class, b::build);
    }

    @Test
    void havingUnknownAliasThrows() {
        var b = Query.groupBy("Article").keys("Category").countAll("n")
            .having("misspelled", SearchOperator.GREATER_THAN, 5);
        assertThrows(IllegalStateException.class, b::build);
    }

    @Test
    void havingOnKeyIsAllowed() {
        var b = Query.groupBy("Article").keys("Category").countAll("n")
            .having("Category", SearchOperator.EQUALS, "tech");
        assertDoesNotThrow(b::build);
    }

    @Test
    void orderByUnknownAliasThrows() {
        var b = Query.groupBy("Article").keys("Category").countAll("n").orderBy("nope");
        assertThrows(IllegalStateException.class, b::build);
    }
```

Add the missing imports at the top of the test file if absent: `iverson.ObjectSearch.SearchClauseType`, `iverson.ObjectSearch.SearchLogic`, and `static org.junit.jupiter.api.Assertions.assertDoesNotThrow`.

In `QueryBuilderTest.java`, DELETE the test(s) referencing `vectorSimilar` (grep the file).

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Java/client && mvn -q test -Dtest=GroupByBuilderTest`
Expected: compile FAILURE (no `not`/`withHavingLogic`).

- [ ] **Step 3: Implement**

In `GroupByBuilder.java`:

```java
    private SearchLogic havingLogic = SearchLogic.AND;

    /** Adds a MUST_NOT WHERE clause (excludes matches before grouping). */
    public GroupByBuilder not(String field, SearchOperator op, Object value) {
        where.add(SearchClause.newBuilder()
            .setProperty(field)
            .setOperator(op)
            .setValue(SearchValues.toSearchValue(value))
            .setClauseType(SearchClauseType.MUST_NOT)
            .build());
        return this;
    }

    /** Sets the logic combining HAVING clauses. Defaults to AND. */
    public GroupByBuilder withHavingLogic(SearchLogic logic) {
        this.havingLogic = logic;
        return this;
    }
```

In `build(String traceId)`, before constructing the request, add validation, and use `havingLogic`:

```java
        java.util.Set<String> aliases = new java.util.HashSet<>();
        for (MetricSpec m : metrics)
            if (!aliases.add(m.getName().toLowerCase(java.util.Locale.ROOT)))
                throw new IllegalStateException("Duplicate metric alias '" + m.getName() + "'.");
        for (String k : keys) aliases.add(k.toLowerCase(java.util.Locale.ROOT));

        for (SearchClause h : having)
            if (!aliases.contains(h.getProperty().toLowerCase(java.util.Locale.ROOT)))
                throw new IllegalStateException("HAVING references '" + h.getProperty()
                    + "', which is neither a metric alias nor a key.");
        for (SearchSort s : orderBy)
            if (!aliases.contains(s.getProperty().toLowerCase(java.util.Locale.ROOT)))
                throw new IllegalStateException("orderBy references '" + s.getProperty()
                    + "', which is neither a metric alias nor a key.");
```

and change `.setLogic(SearchLogic.AND)` on the having query to `.setLogic(havingLogic)`.

In `QueryBuilder.java`, delete the `vectorSimilar(float[])` method on the condition class (line ~234) and any private helper used only by it.

- [ ] **Step 4: Run the Java suite**

Run: `cd Iverson.Clients/Java/client && mvn -q test`
Expected: BUILD SUCCESS. If the Java sample (`Iverson.Clients/Java/sample`) used `vectorSimilar`, update it the same way as the C# sample.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/Java
git commit -m "feat(java): GroupBy Not/HavingLogic, build validation, drop vector clause"
```

---

### Task 4: Python — same changes

**Files:**
- Modify: `Iverson.Clients/Python/iverson_client/group_by.py`
- Modify: `Iverson.Clients/Python/iverson_client/search.py` (delete `vector_similar` at line ~128 and its docstring mention at line ~146)
- Test: `Iverson.Clients/Python/tests/test_query_builder.py` (delete vector tests), create `Iverson.Clients/Python/tests/test_group_by.py` if absent (grep first — if a group-by test file exists, append there instead)

**Interfaces:**
- Produces: `GroupByBuilder.not_(field, op, value)`, `GroupByBuilder.with_having_logic(logic)`, validation in `build()` raising `ValueError`; `QueryBuilder` no longer offers `vector_similar`.

- [ ] **Step 1: Write the failing tests**

```python
import pytest

from iverson_client import group_by
from iverson_client.generated import object_search_pb2 as pb


def test_not_adds_must_not_clause():
    req = (group_by("Article").keys("Category").count_all("n")
           .not_("Category", pb.EQUALS, "spam").build())
    assert req.query.clauses[0].clause_type == pb.MUST_NOT


def test_with_having_logic_or_is_carried():
    req = (group_by("Article").keys("Category").count_all("n")
           .having("n", pb.GREATER_THAN, 5)
           .with_having_logic(pb.OR).build())
    assert req.having.logic == pb.OR


def test_duplicate_metric_alias_raises():
    b = group_by("Article").keys("Category").sum("WordCount").sum("WordCount")
    with pytest.raises(ValueError, match="WordCount_sum"):
        b.build()


def test_having_unknown_alias_raises():
    b = (group_by("Article").keys("Category").count_all("n")
         .having("misspelled", pb.GREATER_THAN, 5))
    with pytest.raises(ValueError, match="misspelled"):
        b.build()


def test_having_on_key_is_allowed():
    (group_by("Article").keys("Category").count_all("n")
     .having("Category", pb.EQUALS, "tech").build())


def test_order_by_unknown_alias_raises():
    b = group_by("Article").keys("Category").count_all("n").order_by("nope")
    with pytest.raises(ValueError, match="nope"):
        b.build()
```

DELETE the tests in `tests/test_query_builder.py` referencing `vector_similar`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Python && python -m pytest tests/test_group_by.py -q`
Expected: FAIL — `not_`/`with_having_logic` missing.

- [ ] **Step 3: Implement**

In `group_by.py`, next to `where`/`having`:

```python
    def not_(self, field: str, op: int, value: object) -> "GroupByBuilder":
        """Add a MUST_NOT WHERE clause (excludes matches before grouping)."""
        self._where.append(_pb.SearchClause(
            property=field,
            operator=op,
            value=_to_search_value(value),
            clause_type=_pb.MUST_NOT,
        ))
        return self

    def with_having_logic(self, logic: int) -> "GroupByBuilder":
        """Set the logic combining HAVING clauses. Default: AND."""
        self._having_logic = logic
        return self
```

Add `self._having_logic = _pb.AND` to `__init__`, change `logic=_pb.AND` in the `having` SearchQuery of `build()` to `logic=self._having_logic`, and add validation at the top of `build()`:

```python
        aliases: set[str] = set()
        for m in self._metrics:
            key = m.name.lower()
            if key in aliases:
                raise ValueError(f"Duplicate metric alias '{m.name}'.")
            aliases.add(key)
        aliases.update(k.lower() for k in self._keys)

        for h in self._having:
            if h.property.lower() not in aliases:
                raise ValueError(
                    f"HAVING references '{h.property}', which is neither a metric alias nor a key.")
        for s in self._order_by:
            if s.property.lower() not in aliases:
                raise ValueError(
                    f"order_by references '{s.property}', which is neither a metric alias nor a key.")
```

In `search.py`, delete the `vector_similar` method and remove it from the docstring operator list.

- [ ] **Step 4: Run the Python suite**

Run: `cd Iverson.Clients/Python && python -m pytest -q`
Expected: PASS. Update the Python sample if it referenced `vector_similar`.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/Python
git commit -m "feat(python): GroupBy not_/having logic, build validation, drop vector clause"
```

---

### Task 5: TypeScript — same changes

**Files:**
- Modify: `Iverson.Clients/TypeScript/src/group-by.ts`
- Modify: `Iverson.Clients/TypeScript/src/search.ts` (delete `vectorSimilar` at line ~134 and its doc-comment mention at line ~146)
- Test: `Iverson.Clients/TypeScript/tests/query-builder.test.ts` (delete vector tests), create `Iverson.Clients/TypeScript/tests/group-by.test.ts` if absent (append if it exists)

**Interfaces:**
- Produces: `GroupByBuilder.not(field, op, value)`, `GroupByBuilder.withHavingLogic(logic)`, validation in `build()` throwing `Error`; `QueryBuilder` no longer offers `vectorSimilar`.

- [ ] **Step 1: Write the failing tests**

```typescript
import { describe, expect, it } from 'vitest';
import { groupBy } from '../src/group-by.js';
import { SearchClauseType, SearchLogic, SearchOperator } from '../generated/object_search.js';

describe('GroupByBuilder validation and additions', () => {
    it('not() adds a MUST_NOT clause', () => {
        const req = groupBy('Article').keys('Category').countAll('n')
            .not('Category', SearchOperator.EQUALS, 'spam').build();
        expect(req.query!.clauses[0].clauseType).toBe(SearchClauseType.MUST_NOT);
    });

    it('withHavingLogic(OR) is carried', () => {
        const req = groupBy('Article').keys('Category').countAll('n')
            .having('n', SearchOperator.GREATER_THAN, 5)
            .withHavingLogic(SearchLogic.OR).build();
        expect(req.having!.logic).toBe(SearchLogic.OR);
    });

    it('throws on duplicate metric aliases', () => {
        const b = groupBy('Article').keys('Category').sum('WordCount').sum('WordCount');
        expect(() => b.build()).toThrow(/WordCount_sum/);
    });

    it('throws on HAVING with an unknown alias', () => {
        const b = groupBy('Article').keys('Category').countAll('n')
            .having('misspelled', SearchOperator.GREATER_THAN, 5);
        expect(() => b.build()).toThrow(/misspelled/);
    });

    it('allows HAVING on a key', () => {
        expect(() => groupBy('Article').keys('Category').countAll('n')
            .having('Category', SearchOperator.EQUALS, 'tech').build()).not.toThrow();
    });

    it('throws on orderBy with an unknown alias', () => {
        const b = groupBy('Article').keys('Category').countAll('n').orderBy('nope');
        expect(() => b.build()).toThrow(/nope/);
    });
});
```

DELETE the tests in `tests/query-builder.test.ts` referencing `vectorSimilar`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/TypeScript && npx vitest run tests/group-by.test.ts`
Expected: FAIL — `not`/`withHavingLogic` missing.

- [ ] **Step 3: Implement**

In `group-by.ts`, add a `_havingLogic` field and the methods:

```typescript
    private _havingLogic: SearchLogic = SearchLogic.AND;

    /** Add a MUST_NOT WHERE clause (excludes matches before grouping). */
    not(field: string, op: SearchOperator, value: unknown): this {
        this._where.push({
            property: field,
            operator: op,
            value: toSearchValue(value),
            clauseType: SearchClauseType.MUST_NOT,
        });
        return this;
    }

    /** Set the logic combining HAVING clauses. Default: AND. */
    withHavingLogic(logic: SearchLogic): this {
        this._havingLogic = logic;
        return this;
    }
```

In `build()`, change the having query's `logic: SearchLogic.AND` to `logic: this._havingLogic` and add validation at the top:

```typescript
        const aliases = new Set<string>();
        for (const m of this._metrics) {
            const key = m.name.toLowerCase();
            if (aliases.has(key)) throw new Error(`Duplicate metric alias '${m.name}'.`);
            aliases.add(key);
        }
        for (const k of this._keys) aliases.add(k.toLowerCase());

        for (const h of this._having) {
            if (!aliases.has(h.property.toLowerCase())) {
                throw new Error(
                    `HAVING references '${h.property}', which is neither a metric alias nor a key.`);
            }
        }
        for (const s of this._orderBy) {
            if (!aliases.has(s.property.toLowerCase())) {
                throw new Error(
                    `orderBy references '${s.property}', which is neither a metric alias nor a key.`);
            }
        }
```

In `search.ts`, delete the `vectorSimilar` method and its doc-comment mention.

- [ ] **Step 4: Run the TS suite**

Run: `cd Iverson.Clients/TypeScript && npx vitest run && npx tsc --noEmit`
Expected: PASS. Update the TS sample if it referenced `vectorSimilar`.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/TypeScript
git commit -m "feat(typescript): GroupBy not/having logic, build validation, drop vector clause"
```

---

### Task 6: Go — same changes

**Files:**
- Modify: `Iverson.Clients/Go/iverson/group_by.go`
- Modify: `Iverson.Clients/Go/iverson/search.go` (delete `FieldCondition.VectorSimilar` at line ~195)
- Test: `Iverson.Clients/Go/iverson_test/search_test.go` (delete vector tests), create `Iverson.Clients/Go/iverson_test/group_by_test.go` if absent (append if it exists)

**Interfaces:**
- Produces: `GroupByBuilder.Not(field string, op pb.SearchOperator, val *pb.SearchValue)`, `GroupByBuilder.WithHavingLogic(logic pb.SearchLogic)`, validation in `Build()` returning an error; `FieldCondition` no longer offers `VectorSimilar`.

- [ ] **Step 1: Write the failing tests**

```go
package iverson_test

import (
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
)

func strVal(s string) *pb.SearchValue {
	return &pb.SearchValue{Kind: &pb.SearchValue_StringVal{StringVal: s}}
}

func TestGroupByNotAddsMustNot(t *testing.T) {
	req, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").
		Not("Category", pb.SearchOperator_EQUALS, strVal("spam")).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Query.Clauses[0].ClauseType != pb.SearchClauseType_MUST_NOT {
		t.Errorf("clause type = %v", req.Query.Clauses[0].ClauseType)
	}
}

func TestGroupByWithHavingLogicOr(t *testing.T) {
	req, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").
		Having("n", pb.SearchOperator_GREATER_THAN, numberVal(5)).
		WithHavingLogic(pb.SearchLogic_OR).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if req.Having.Logic != pb.SearchLogic_OR {
		t.Errorf("having logic = %v", req.Having.Logic)
	}
}

func TestGroupByDuplicateMetricAliasErrors(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").Sum("WordCount").Sum("WordCount").Build()
	if err == nil {
		t.Fatal("expected duplicate alias error")
	}
}

func TestGroupByHavingUnknownAliasErrors(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").
		Having("misspelled", pb.SearchOperator_GREATER_THAN, numberVal(5)).
		Build()
	if err == nil {
		t.Fatal("expected unknown having alias error")
	}
}

func TestGroupByHavingOnKeyAllowed(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").
		Having("Category", pb.SearchOperator_EQUALS, strVal("tech")).
		Build()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
}

func TestGroupByOrderByUnknownAliasErrors(t *testing.T) {
	_, err := iverson.NewGroupBy("Article").
		Keys("Category").CountAll("n").OrderBy("nope").Build()
	if err == nil {
		t.Fatal("expected unknown orderBy alias error")
	}
}
```

(If `numberVal` already exists in the test package from the pipeline plan's task, reuse it; otherwise include it. Go duplicate-function compile errors will flag it immediately.)

DELETE the tests in `search_test.go` referencing `VectorSimilar`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd Iverson.Clients/Go && go test ./iverson_test/ -run TestGroupBy`
Expected: compile FAILURE (no `Not`/`WithHavingLogic`).

- [ ] **Step 3: Implement**

In `group_by.go`, add a `havingLogic` field (initialized in `NewGroupBy` to `pb.SearchLogic_AND`) and:

```go
// Not adds a MUST_NOT WHERE clause (excludes matches before grouping).
func (g *GroupByBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *GroupByBuilder {
	if val == nil {
		g.err = fmt.Errorf("field %q: nil search value for operator %v", field, op)
		return g
	}
	g.where = append(g.where, &pb.SearchClause{
		Property:   field,
		Operator:   op,
		Value:      val,
		ClauseType: pb.SearchClauseType_MUST_NOT,
	})
	return g
}

// WithHavingLogic sets the logic combining HAVING clauses. Default: AND.
func (g *GroupByBuilder) WithHavingLogic(logic pb.SearchLogic) *GroupByBuilder {
	g.havingLogic = logic
	return g
}
```

In `Build`, change the having query's `Logic: pb.SearchLogic_AND` to `Logic: g.havingLogic`, and add validation after the `g.err` check:

```go
	aliases := map[string]bool{}
	for _, m := range g.metrics {
		key := strings.ToLower(m.Name)
		if aliases[key] {
			return nil, fmt.Errorf("duplicate metric alias %q", m.Name)
		}
		aliases[key] = true
	}
	for _, k := range g.keys {
		aliases[strings.ToLower(k)] = true
	}
	for _, h := range g.having {
		if !aliases[strings.ToLower(h.Property)] {
			return nil, fmt.Errorf(
				"HAVING references %q, which is neither a metric alias nor a key", h.Property)
		}
	}
	for _, s := range g.orderBy {
		if !aliases[strings.ToLower(s.Property)] {
			return nil, fmt.Errorf(
				"OrderBy references %q, which is neither a metric alias nor a key", s.Property)
		}
	}
```

(add `"strings"` to the imports). In `search.go`, delete the `VectorSimilar` method on `FieldCondition`.

- [ ] **Step 4: Run the Go suite**

Run: `cd Iverson.Clients/Go && go build ./... && go test ./...`
Expected: PASS. Update the Go sample if it referenced `VectorSimilar`.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Clients/Go
git commit -m "feat(go): GroupBy Not/HavingLogic, build validation, drop vector clause"
```

---

### Task 7: Documentation updates

**Files:**
- Modify: `docs/one-query-five-languages.md`

**Interfaces:** Reflects Tasks 1–6; verify each claim against the merged code.

- [ ] **Step 1: Update the capability matrix and operator table**

- In the operator table's "Suffix" row, change the C# cell from `` `SearchOperator.EndsWith` ¹ `` to `` `EndsWith` `` and delete footnote ¹ (both under the table and in the capability matrix's `endsWith` row).
- In the capability matrix, change `endsWith` C# cell from `✅ ¹` to `✅`.

- [ ] **Step 2: Replace the vectorSimilar heads-up**

Replace the blockquote near the end of the semantic-search section (`> **Heads-up:** the query builders still expose a vectorSimilar clause ...`) with:

```markdown
> **Removed:** the query builders no longer expose a `vectorSimilar` clause, and the server
> rejects `VECTOR_SIMILAR` clauses on the SQL paths with `INVALID_ARGUMENT`. Vector search
> goes through `SearchSimilar` / `SearchChunks` exclusively.
```

- [ ] **Step 3: Document the new GroupBy surface and build-time validation**

In the GroupBy section, after the metrics reference table, add:

```markdown
### Build-time validation

Builders now fail fast instead of silently dropping configuration:

- `GroupByBuilder` rejects duplicate metric aliases, and any `having`/`orderBy` reference
  that is neither a declared metric alias nor a key — at `build()`, before any RPC.
- `GroupByBuilder` gains `not(...)` (MUST_NOT clauses) and `withHavingLogic(...)` (OR-combined
  HAVING) in all five languages.
- C#'s `QueryBuilder<T>.Build()` throws if aggregations were configured (call
  `BuildAggregate()` instead), and `BuildAggregate()` throws if sorting or paging was
  configured (they have no effect on aggregations).
- C# joins support multi-hop chains: `Join<TAuthor, TPublisher>(au => au.PublisherId, p => p.Id)`
  joins from an already-joined type.
```

- [ ] **Step 4: Commit**

```bash
git add docs/one-query-five-languages.md
git commit -m "docs: DSL validation, GroupBy additions, vector clause removal"
```
