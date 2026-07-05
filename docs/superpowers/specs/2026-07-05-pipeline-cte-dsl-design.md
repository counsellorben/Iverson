# Pipeline CTE DSL — Design

**Date:** 2026-07-05
**Status:** Approved design
**Supersedes:** `docs/pipeline-aggregations-proposal.md` (2026-06-27 proposal; step-shape, joins, and naming decisions below override it)

## Problem

`AggregateAsync` runs each spec as an independent parallel query and `GroupBy` emits one compound SELECT — neither can express computations where one result feeds the next: top-N per group, running totals, derived ratios, filter-then-aggregate, rank-then-limit, bucket-then-metric. All are natural CTE chains in StarRocks SQL. This project adds a `Pipeline` RPC and fluent builders in all five client languages that compile step chains to a single CTE-chain query, plus a set of correctness improvements to the existing fluent DSL.

## Decisions (settled in this design or carried from the 2026-06-27 session)

1. **Explicit CTE boundaries.** Each `.Step("name", s => ...)` is exactly one CTE; the step name becomes the CTE name. Implicit server-side boundary inference was rejected as a leaky abstraction.
2. **`reads` makes the chain a DAG.** A step reads the immediately preceding step by default; `.Reads("stepName")` selects any earlier named step (base included). Forward and self references are validation errors.
3. **Full joins in v1.** A step may join its input against any prior step's CTE or any registered entity table. This forces the projection API and per-step column tracking into scope (below).
4. **SQL-shaped steps.** One step = one SELECT: WHERE + (window functions XOR GroupBy/metrics/HAVING) + derive columns + joins + projection. Windows and GROUP BY are mutually exclusive within a step; a window over aggregated output is the next step.
5. **All five clients in v1.** C#, Java, Python, TypeScript, Go all ship a pipeline builder.
6. **String-based field addressing** in all five builders (the `GroupByBuilder` precedent — multiple sources in scope). C# gets `Pipeline.For<T>()` sugar for the type name only.
7. **Existing-DSL improvements are folded into this project** (see "Fluent DSL improvements" below).
8. **Response reuses `SearchResponse`** streaming (score constant `1.0`, like GroupBy) so all clients' existing streaming plumbing works unchanged.

## DSL surface

### Pipeline structure

- **Base step** (implicit, named `"base"`): SELECT from the entity's table; `.Where(...)` clauses before the first `.Step()` filter it.
- **Named steps**: `.Step("name", s => ...)`, each one CTE.
- **Final ordering/paging**: request-level `.SortOn(alias)` / `.Limit(n)` (default 10,000, matching GroupBy). Per-step LIMIT is rejected in v1; top-N-per-group is the documented `RowNumber` + `Where` pattern.

### Step contents (each optional, combined into one SELECT)

| Builder call | SQL | Notes |
|---|---|---|
| `.Where(field, op, value)` / `.Not(...)` / `.WithLogic(...)` | `WHERE` | References input columns *or prior-step aliases* |
| `.RowNumber/Rank/DenseRank(alias, partitionBy?, orderBy, desc?)` | `ROW_NUMBER()/RANK()/DENSE_RANK() OVER (...)` | XOR with GroupBy |
| `.RunningSum/RunningAvg(alias, field, orderBy)` | `SUM/AVG(field) OVER (ORDER BY ...)` | |
| `.Lag/Lead(alias, field, offset)` | `LAG/LEAD(field, n) OVER (...)` | |
| `.GroupBy(field, dateTrunc?)` | `GROUP BY` (+ `DATE_TRUNC`) | Composite keys via repeated calls; `DateTrunc`: minute→year |
| `.Sum/Avg/Min/Max/Count(field, alias?)`, `.CountAll(alias)`, `.SumExpr/AvgExpr(expr, alias)` | aggregate metrics | Reuses `MetricSpec` semantics incl. raw expressions |
| `.Having(alias, op, value)` | `HAVING` | References metric aliases |
| `.Derive(alias, expr)` | scalar expression column | Validated: known column refs, arithmetic, parens, whitelisted functions; no subqueries/semicolons |
| `.Reads("step")` | `FROM step` | Default: previous step |
| `.Join(source, on: (left, right), kind?)` | `JOIN ... ON l = r` | `source` = prior step name or registered type name; multiple `on` pairs AND-ed; INNER/LEFT/RIGHT/FULL; non-equality conditions out of scope |
| `.Select(p => p.AllFrom(source).Pick(source, col, alias?))` | projection | Required semantics only when joins present; default = all input columns + new aliases |

### Example

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
        .Join("Author", on: ("AuthorId", "Id"))
        .Select(p => p.AllFrom("ranked").Pick("Author", "Name")))
    .SortOn("rank")
    .Limit(5);
```

```sql
WITH base AS (
  SELECT ... FROM `articles` WHERE `IsPublished` = @s0_p0
),
by_author AS (
  SELECT `AuthorId`, COUNT(*) AS `articles`
  FROM base GROUP BY `AuthorId` HAVING `articles` > @s1_h0
),
ranked AS (
  SELECT *, ROW_NUMBER() OVER (ORDER BY `articles` DESC) AS `rank`
  FROM by_author
),
named AS (
  SELECT ranked.*, `a`.`Name`
  FROM ranked JOIN `authors` a ON `ranked`.`AuthorId` = `a`.`Id`
)
SELECT * FROM named ORDER BY `rank` ASC LIMIT 5
```

## Proto contract

One new RPC on `ObjectSearchService`, streaming `SearchResponse` rows:

```protobuf
rpc Pipeline (PipelineRequest) returns (stream SearchResponse);

message PipelineRequest {
    string                type_name  = 1;
    repeated SearchClause base_where = 2;   // WHERE on the implicit base step
    SearchLogic           base_logic = 3;
    repeated PipelineStep steps      = 4;
    repeated SearchSort   order_by   = 5;   // final ORDER BY (aliases or columns)
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
    repeated SearchClause   having      = 8;  // property = metric alias
    repeated DeriveColumn   derive      = 9;
    repeated PipelineJoin   joins       = 10;
    repeated SelectItem     select      = 11; // empty = all input columns + new aliases
}

message PipelineJoin {
    string   source = 1;              // prior step name OR registered type name
    JoinKind kind   = 2;              // reuses existing enum
    repeated JoinCondition on = 3;    // equality pairs, AND-ed
}
message JoinCondition  { string left = 1; string right = 2; }
message SelectItem     { string source = 1; string column = 2; bool all = 3; string alias = 4; }
message GroupKey       { string field = 1; DateTrunc date_trunc = 2; }
message DeriveColumn   { string alias = 1; string expr = 2; }

message WindowFunction {
    string             alias        = 1;
    WindowFunctionKind kind         = 2;
    string             field        = 3;  // source column/alias (empty for ROW_NUMBER/RANK/DENSE_RANK)
    string             partition_by = 4;
    string             order_by     = 5;
    bool               descending   = 6;
    int32              offset       = 7;  // LAG/LEAD
}
enum WindowFunctionKind { ROW_NUMBER = 0; RANK = 1; DENSE_RANK = 2; RUNNING_SUM = 3; RUNNING_AVG = 4; LAG = 5; LEAD = 6; }
enum DateTrunc { NONE = 0; MINUTE = 1; HOUR = 2; DAY = 3; WEEK = 4; MONTH = 5; QUARTER = 6; YEAR = 7; }
```

## Server compilation

`StarRocksQueryBuilder.BuildPipeline(tableName, schema, request, registry)` — two passes.

**Pass 1 — column tracking.** Walk steps in order, computing each step's output column set:
- base → schema columns (via the existing case-insensitive `ResolveColumn` index)
- non-aggregate step → input set + window/derive aliases, or the `select` projection if present
- aggregate step → group keys + metric aliases (replaces input set)
- joined step → resolved per its `select` items; `all=true` items expand from that source's tracked set (this is why tracking is mandatory with joins in scope)

Every column reference — WHERE, join conditions, partition/order fields, HAVING aliases, derive expressions, final sort — resolves against the correct input set for its position.

**Pass 2 — SQL emission.** `WITH name AS (SELECT ...), ...` then `SELECT * FROM lastStep ORDER BY ... LIMIT n`. Parameter prefixes: `s{stepIdx}_p{n}` (WHERE) and `s{stepIdx}_h{n}` (HAVING) so multi-step parameters never collide in one `DynamicParameters`. Reuses `BuildWhere`/`BuildHaving`/`QuoteQualified`/`EscapeIdentifier`; `BuildWhere` gains an overload resolving against a tracked column set instead of only a `SchemaDescriptor` (step WHEREs reference prior-step aliases).

**Validation rules** (all gRPC `InvalidArgument`, all before SQL emission, errors name the step and the offending reference):
1. Step names unique, valid identifiers, and not colliding with registered type names.
2. `reads` / join `source` must name an earlier step or a registered type — no forward/self references.
3. Windows XOR aggregation within a step.
4. Every column/alias reference resolvable in its input set; ambiguous bare names in joined steps rejected (same policy as existing join resolution).
5. `Derive` expressions restricted: known column references, arithmetic, parens, whitelisted functions; no subqueries or semicolons.
6. Duplicate output aliases within a step rejected.

**Execution.** `ObjectSearchGrpcService.Pipeline` follows the `GroupBy` method's pattern exactly: readiness gate, resilience pipeline, Dapper streaming, `Struct` conversion, W3C trace propagation.

## Client surfaces (all five)

| Language | Entry | Step | Notes |
|----------|-------|------|-------|
| C# | `Pipeline.For("Article")` / `Pipeline.For<T>()` | `.Step("name", s => ...)` | `EntityCoordinator<T>` gains `PipelineAsync` (dict rows) + `PipelineAsync<TResult>` (StructConverter projection) |
| Java | `Query.pipeline("Article")` | `.step("name", s -> ...)` | `reads` (never `from` — keyword); blocking-stub streaming like `groupBy` |
| Python | `pipeline("Article")` | `.step("name", lambda s: ...)` | `.reads(...)` method (never `from` — keyword); snake_case methods |
| TypeScript | `pipeline('Article')` | `.step('name', s => ...)` | async-iterable streaming like `groupBy` |
| Go | `iverson.NewPipeline("Article")` | `.Step("name", func(s *StepBuilder) {...})` | error accumulation surfaced at `Build()`, matching its QueryBuilder |

Client-side pre-validation (no schema needed): step-name uniqueness, reads-must-be-earlier, windows-XOR-aggregation, duplicate aliases within a step. Column existence stays server-side. `build()` remains offline in every language.

## Fluent DSL improvements (folded into this project)

1. **Build-time validation in existing builders.** `QueryBuilder<T>.Build()` throws if aggregations were configured (silently dropped today); `BuildAggregate()` throws if paging/sort was configured. All five `GroupByBuilder`s validate at build: `Having`/`OrderBy` alias references must be a declared metric alias or key; duplicate metric aliases rejected.
2. **`GroupByBuilder.Not(...)`** (MustNot clauses) in all five languages.
3. **C# `SearchOperators.EndsWith` static alias** — closes the documented capability-matrix footnote.
4. **Multi-hop joins in C# `QueryBuilder<T>`** — `Join<TLeft, TRight>` overload setting `LeftType = typeof(TLeft).Name`; proto and server already support it.
5. **Dead `VectorSimilar` clauses fail loudly.** Server returns `InvalidArgument` on `Search`/`GroupBy`/`Aggregate` instead of silently skipping; `WhereVectorSimilar`/`NotVectorSimilar` removed from all client builders (breaking cleanup, same precedent as must/should removal; `SearchSimilar`/`SearchChunks` are the real vector path).
6. **`GroupByBuilder.WithHavingLogic(...)`** — HAVING logic is hardcoded AND today.

## Testing

- **Server unit tests**: SQL-generation golden tests for `BuildPipeline` (each step kind, reads-DAG, CTE-vs-CTE and CTE-vs-entity joins, projection expansion, parameter prefixing) plus one test per validation rule asserting `InvalidArgument`.
- **Integration tests** (live StarRocks, existing suite): the motivating scenarios end-to-end — top-N per group, running total, derived ratio, filter-then-aggregate, join-enrichment.
- **Client tests**: per-language builder tests (proto snapshot equality + pre-validation errors), following each client's existing test layout.
- **DSL-improvement tests**: each behavior change (throwing builds, `Not`, `EndsWith`, multi-hop join, VectorSimilar rejection) gets tests in the affected languages/server.

## Documentation

- New pipeline section in `docs/one-query-five-languages.md` (same same-query-five-ways format); gotchas updated (VectorSimilar builders removed; per-step LIMIT restriction).
- `docs/pipeline-aggregations-proposal.md` marked superseded by this spec.

## Out of scope for v1

- Non-equality join conditions (range joins, expression joins)
- Per-step LIMIT (use `RowNumber` + `Where`)
- UNION between steps; sub-pipelines; parameterized stored pipelines
- Window frames (`ROWS BETWEEN ...`) beyond the fixed function set
- Typed expression-tree step fields in C#/Java/Python (string addressing everywhere; revisit after v1 usage)

## Implementation decomposition

Three plans (writing-plans stage): **(1) proto + server** — contract, BuildPipeline, column tracking, validation, gRPC method, server tests; **(2) five client builders** — per-language surfaces, coordinator helpers, client tests, docs; **(3) fluent DSL improvements** — the six items above across server and clients. Plans 2 and 3 depend on 1; 2 and 3 are mutually independent.
