# Iverson Search DSL

**One query model. Five languages. Two engines.**

Every Iverson client — C#, Java, Python, TypeScript, and Go — ships a fluent query builder that compiles to the same protobuf contract ([`object_search.proto`](../Iverson.Clients/Common/Proto/object_search.proto)). The server routes each RPC to the engine best suited for the job:

- **StarRocks** answers everything SQL-shaped: filters, sorting, paging, joins, and analytics — sub-second even over hundreds of millions of rows.
- **Qdrant** answers everything meaning-shaped: semantic entity search and passage-level RAG retrieval, with embeddings computed **server-side** so your client never touches a model.

Because the DSL compiles to a proto message, `build()` never needs a live server — you can construct, inspect, and unit-test queries offline in any language.

## Choosing your entry point

| You want | Call | Engine | Returns |
|----------|------|--------|---------|
| Rows matching filters, sorted and paged | `Search` | StarRocks | stream of entities |
| Multiple metrics per group, one round trip | `GroupBy` | StarRocks | stream of result rows |
| Bucketed facets (terms, date histogram, ranges) | `Aggregate` | StarRocks | buckets + metrics |
| Multi-step analytics: top-N per group, running totals, ratios | `Pipeline` | StarRocks | stream of result rows |
| "Find articles *about* this" from raw text | `SearchSimilar` | Qdrant | stream of entities + scores |
| The best *passages* for a RAG prompt | `SearchChunks` | Qdrant | stream of chunks + scores |

## The sample model

Examples below use a small blog domain. Field names are shown PascalCase; the server resolves them **case-insensitively**, so each language can follow its own conventions.

```
Article:  Id, Title, Body, Category, WordCount, PublishedAt, IsPublished, AuthorId
Author:   Id, Name, Email, Bio
```

---

## The same query, five ways

*Published articles in the technology category with at least 500 words, newest first, first 10.*

### C#

Strongly typed, expression-based — typos become compile errors. `EntityCoordinator<T>.SearchAsync` executes the query and streams typed entities back.

```csharp
using Iverson.Client.Search;
using static Iverson.Client.Search.SearchOperators;

var query = Query.For<Article>()
    .Where(a => a.IsPublished, EqualTo,     true)
    .Where(a => a.Category,    EqualTo,     "technology")
    .Where(a => a.WordCount,   GreaterThanOrEquals, 500)
    .OrderBy(a => a.PublishedAt, descending: true)
    .Page(0, size: 10);

await foreach (var r in articles.SearchAsync(query))   // EntityCoordinator<Article> from DI
    Console.WriteLine(r.Entity.Title);
```

### Java

Field-name strings with a fluent condition step. `EntityCoordinator.search` executes and returns typed results.

```java
import io.iverson.client.search.Query;

var results = articleCoordinator.search(
    Query.of(Article.class)
        .where("IsPublished").eq(true)
        .where("Category").eq("technology")
        .where("WordCount").gte(500)
        .orderByDesc("PublishedAt")
        .limit(10));

results.forEach(r -> System.out.println(r.entity().getTitle()));
```

### Python

```python
import grpc
from iverson_client import QueryBuilder
from iverson_client.generated import object_search_pb2_grpc as search_grpc

request = (
    QueryBuilder("Article")
    .where("IsPublished").eq(True)
    .where("Category").eq("technology")
    .where("WordCount").gte(500)
    .order_by_desc("PublishedAt")
    .limit(10)
    .build()
)

channel = grpc.insecure_channel("localhost:5000")
search = search_grpc.ObjectSearchServiceStub(channel)
for response in search.Search(request):        # streams SearchResponse
    print(response.data)                       # google.protobuf.Struct
```

### TypeScript

Builders come from the package barrel (`src/index.ts`); the gRPC service client is in the generated code (`generated/object_search.ts`) — adjust import paths to your build setup.

```typescript
import { credentials } from '@grpc/grpc-js';
import { QueryBuilder } from '@iverson/client';
import { ObjectSearchServiceClient } from '@iverson/client/generated/object_search.js';

const request = new QueryBuilder('Article')
    .where('IsPublished').eq(true)
    .where('Category').eq('technology')
    .where('WordCount').gte(500)
    .orderByDesc('PublishedAt')
    .limit(10)
    .build();

const search = new ObjectSearchServiceClient('localhost:5000', credentials.createInsecure());
for await (const response of search.search(request)) {
    console.log(response.data);
}
```

### Go

The builder collects errors internally and surfaces them once at `Build()` — no error handling mid-chain.

```go
import (
    "github.com/iverson/clients/go/iverson"
    pb "github.com/iverson/clients/go/generated"
)

req, err := iverson.NewQuery("Article").
    Where("IsPublished").Eq(true).
    Where("Category").Eq("technology").
    Where("WordCount").Gte(500).
    OrderByDesc("PublishedAt").
    Limit(10).
    Build()
if err != nil { /* handle */ }

stream, err := client.SearchStub.Search(ctx, req)   // *iverson.IversonClient
for {
    resp, err := stream.Recv()
    if err == io.EOF { break }
    if err != nil { /* handle */ }
    fmt.Println(resp.Data)
}
```

---

## Clauses: filtering the result set

Every clause has a **field**, an **operator**, and a **value**, and is either a required predicate or a negated one:

| Proto | C# | Java | Python | TypeScript | Go |
|-------|----|------|--------|------------|----|
| `FILTER` | `.Where(...)` | `.where(f)` | `.where(f)` | `.where(f)` | `.Where(f)` |
| `MUST_NOT` | `.Not(...)` | `.not(f)` | `.must_not(f)` | `.mustNot(f)` | `.MustNot(f)` |

There is no relevance boosting — `Search` returns a constant score of `1.0` for every row, and result order is controlled entirely by `OrderBy`. To make clauses alternatives instead of requirements, switch the combination logic to OR (below).

### Combination logic

By default all clauses are ANDed. `withLogic` flips the top level to OR:

```csharp
// C# — match any of three categories
Query.For<Article>()
    .Where(a => a.Category, EqualTo, "basketball")
    .Where(a => a.Category, EqualTo, "culture")
    .Where(a => a.Category, EqualTo, "legacy")
    .WithLogic(SearchLogic.Or);
```

```python
# Python
from iverson_client.generated import object_search_pb2 as search_pb

QueryBuilder("Article") \
    .where("Category").eq("basketball") \
    .where("Category").eq("culture") \
    .with_logic(search_pb.OR)
```

(For a "match any of a fixed set" query, the `IN` operator is usually the cleaner tool.)

### Operators

| Operation | SQL emitted | C# constant | Java | Python | TypeScript | Go |
|-----------|-------------|-------------|------|--------|------------|----|
| Equals | `=` | `EqualTo` | `.eq(v)` | `.eq(v)` | `.eq(v)` | `.Eq(v)` |
| Not equals | `<>` | `NotEquals` | `.neq(v)` | `.neq(v)` | `.neq(v)` | `.NotEq(v)` |
| Substring | `LIKE '%v%'` | `Contains` | `.contains(v)` | `.contains(v)` | `.contains(v)` | `.Contains(v)` |
| Prefix | `LIKE 'v%'` | `StartsWith` | `.startsWith(v)` | `.starts_with(v)` | `.startsWith(v)` | `.StartsWith(v)` |
| Suffix | `LIKE '%v'` | `SearchOperator.EndsWith` ¹ | `.endsWith(v)` | `.ends_with(v)` | `.endsWith(v)` | `.EndsWith(v)` |
| Greater than | `>` | `GreaterThan` | `.gt(v)` | `.gt(v)` | `.gt(v)` | `.Gt(v)` |
| Less than | `<` | `LessThan` | `.lt(v)` | `.lt(v)` | `.lt(v)` | `.Lt(v)` |
| At least | `>=` | `GreaterThanOrEquals` | `.gte(v)` | `.gte(v)` | `.gte(v)` | `.Gte(v)` |
| At most | `<=` | `LessThanOrEquals` | `.lte(v)` | `.lte(v)` | `.lte(v)` | `.Lte(v)` |
| One of a set | `IN (...)` | `In` | `.in(v...)` | `.in_(list)` | `.in(list)` | `.In(v...)` |

¹ The proto enum has `ENDS_WITH` and the C# builder accepts it (`.Where(a => a.Title, SearchOperator.EndsWith, "era")`), but `SearchOperators` has no static `EndsWith` alias yet.

**Values.** Strings, numbers, and booleans map to the typed proto union. **Dates travel as ISO 8601 strings** (C# serializes `DateTime` with the round-trip `"o"` format automatically; other languages pass strings like `"2026-06-01T00:00:00Z"`). `IN` accepts a list of strings.

```csharp
// C# — date range
Query.For<Article>()
    .Where(a => a.PublishedAt, GreaterThan,      DateTime.UtcNow.AddDays(-30))
    .Where(a => a.PublishedAt, LessThanOrEquals, DateTime.UtcNow)

// C# — IN
Query.For<Article>()
    .Where(a => a.Category, In, new[] { "technology", "science", "ai" })
```

```go
// Go — IN is variadic
iverson.NewQuery("Article").Where("Category").In("technology", "science", "ai")
```

---

## Sorting, paging, and projection

### Sorting

Multiple sorts stack in the order added, in every language:

```typescript
new QueryBuilder('Article')
    .orderByDesc('PublishedAt')   // primary
    .orderBy('Title')             // secondary, ascending
```

### Paging

Page size defaults to 20 everywhere. Every client's page parameter is **0-based** — `page`/`offset` of `0` is the first page — matching the server's `OFFSET page × size` computation:

| Language | Call | Convention |
|----------|------|------------|
| C# | `.Page(page, size)` | 0-based page |
| Java | `.limit(n).offset(page)` | 0-based page |
| Python | `.limit(n).offset(page)` | 0-based page |
| TypeScript | `.limit(n).offset(page)` | 0-based page |
| Go | `.Limit(n).Offset(page)` | 0-based page |

### Field projection

Trim the payload to just the columns you need — the server emits an explicit `SELECT` list:

```python
QueryBuilder("Article").fields("Title", "PublishedAt").limit(50)
```

Available as `fields(...)` in Java/Python/TypeScript and `Fields(...)` in Go. C#'s `QueryBuilder<T>` has a typed equivalent: `.Fields(a => a.Title, a => a.PublishedAt)`.

---

## Joins

Any registered type can join to another on matching fields — the server resolves the physical tables and emits real SQL joins. All string-based builders share the same shape:

```
join(leftField, rightType, rightField [, kind])     // kind: INNER (default) | LEFT | RIGHT | FULL
```

```typescript
// TypeScript — articles with their authors
new QueryBuilder('Article')
    .join('AuthorId', 'Author', 'Id')
    .where('Author.Name').contains('Iverson')
    .fields('Title', 'Author.Name')
```

C# gets a typed variant on `QueryBuilder<T>`:

```csharp
Query.For<Article>()
    .Join<Author>(a => a.AuthorId, au => au.Id)         // JoinKind.Inner by default
    .Where(a => a.Title, Contains, "crossover");
```

**Disambiguation:** once a join is in play, reference fields as `TypeName.FieldName` (`"Author.Name"`). Bare names still work when they're unambiguous across the joined schemas; ambiguous bare names are rejected rather than guessed.

---

## GroupBy: analytics in one round trip

The `GroupBy` RPC is the DSL's analytics workhorse: several metrics over the same grouping, plus WHERE, HAVING, JOIN, ORDER BY, and LIMIT — compiled to **one compound SELECT**. (This is the shape of TPC-H Q1.) It's available in all five clients via `GroupByBuilder`, which is string-based everywhere — joins bring multiple types into scope, so fields are addressed by name.

*Top authors by published-article count, with their latest publish date — at least 3 articles, top 10:*

### C#

```csharp
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using static Iverson.Client.Search.SearchOperators;

var request = Query.GroupBy("Article")
    .Join("AuthorId", "Author", "Id")
    .Keys("Author.Name")
    .CountAll("articles")
    .Max("PublishedAt", "latest")
    .Where("IsPublished", EqualTo, true)
    .Having("articles", GreaterThanOrEquals, 3)
    .OrderBy("articles", descending: true)
    .Limit(10)
    .Build();

// ObjectSearchService.ObjectSearchServiceClient from DI
using var call = searchClient.GroupBy(request);
await foreach (var row in call.ResponseStream.ReadAllAsync())
    Console.WriteLine(row.Data);          // Struct: { Name, articles, latest }
```

### Java

```java
import io.iverson.client.search.Query;
import iverson.ObjectSearch.SearchOperator;

var request = Query.groupBy("Article")
    .join("AuthorId", "Author", "Id")
    .keys("Author.Name")
    .countAll("articles")
    .max("PublishedAt", "latest")
    .where("IsPublished", SearchOperator.EQUALS, true)
    .having("articles", SearchOperator.GREATER_THAN_OR_EQUALS, 3)
    .orderByDesc("articles")
    .limit(10)
    .build();

// Build your own stub on the same channel used by IversonClient
var search = ObjectSearchServiceGrpc.newBlockingStub(channel);
search.groupBy(request).forEachRemaining(row -> System.out.println(row.getData()));
```

### Python

```python
from iverson_client import group_by
from iverson_client.search import SearchOperator

request = (
    group_by("Article")
    .join("AuthorId", "Author", "Id")
    .keys("Author.Name")
    .count_all("articles")
    .max("PublishedAt", "latest")
    .where("IsPublished", SearchOperator.EQUALS, True)
    .having("articles", SearchOperator.GREATER_THAN_OR_EQUALS, 3)
    .order_by_desc("articles")
    .limit(10)
    .build()
)

for row in search.GroupBy(request):
    print(row.data)
```

### TypeScript

```typescript
import { groupBy, SearchOperator } from '@iverson/client';

const request = groupBy('Article')
    .join('AuthorId', 'Author', 'Id')
    .keys('Author.Name')
    .countAll('articles')
    .max('PublishedAt', 'latest')
    .where('IsPublished', SearchOperator.EQUALS, true)
    .having('articles', SearchOperator.GREATER_THAN_OR_EQUALS, 3)
    .orderByDesc('articles')
    .limit(10)
    .build();

for await (const row of search.groupBy(request)) {
    console.log(row.data);
}
```

### Go

Go's `Where`/`Having` take raw `*pb.SearchValue` — construct the proto union directly:

```go
req, err := iverson.NewGroupBy("Article").
    Join("AuthorId", "Author", "Id").
    Keys("Author.Name").
    CountAll("articles").
    Max("PublishedAt", "latest").
    Where("IsPublished", pb.SearchOperator_EQUALS,
        &pb.SearchValue{Kind: &pb.SearchValue_BoolVal{BoolVal: true}}).
    Having("articles", pb.SearchOperator_GREATER_THAN_OR_EQUALS,
        &pb.SearchValue{Kind: &pb.SearchValue_NumberVal{NumberVal: 3}}).
    OrderByDesc("articles").
    Limit(10).
    Build()

stream, err := client.SearchStub.GroupBy(ctx, req)
```

### Metrics reference

| Builder method | SQL | Default alias |
|----------------|-----|---------------|
| `sum(field)` / `avg` / `min` / `max` | `SUM(field)` … | `{field}_sum` … |
| `count(field)` | `COUNT(field)` | `{field}_count` |
| `countAll()` | `COUNT(*)` | `count` |
| `sumExpr(expr, alias)` / `avgExpr(expr, alias)` | `SUM(expr)` | required |

Every metric takes an optional alias; **HAVING and ORDER BY reference aliases**, so name metrics you intend to filter or sort on. Expression metrics accept raw SQL over joined columns — the TPC-H revenue classic:

```csharp
Query.GroupBy("LineItem")
    .Keys("ReturnFlag", "LineStatus")
    .Sum("Quantity", "sum_qty")
    .SumExpr("ExtendedPrice * (1 - Discount)", "revenue")
    .CountAll("order_count")
    .OrderBy("ReturnFlag")
```

Results stream back as `SearchResponse` rows whose `Data` struct holds the group keys plus one entry per metric alias. Default row limit: 10,000.

---

## Aggregate: bucketed facets

Where `GroupBy` returns *rows*, `Aggregate` returns *shaped facets*: terms buckets, calendar histograms, and explicit numeric ranges, each with document counts — plus single-value metrics. Each `AggregationSpec` runs as its own SQL query, so prefer `GroupBy` when you want many metrics over one grouping.

The C# `QueryBuilder<T>` doubles as the aggregate builder — clauses become the pre-aggregation filter:

```csharp
var request = Query.For<Article>()
    .Where(a => a.IsPublished, EqualTo, true)
    .GroupBy(a => a.Category, size: 20)                          // terms buckets
    .ByDateInterval(a => a.PublishedAt, "month",
        timeZone: "America/New_York")                            // calendar histogram
    .ByRange(a => a.WordCount,
        ("short",  null,  500),
        ("medium", 500,   2000),
        ("long",   2000,  null))                                 // explicit ranges
    .Avg(a => a.WordCount)                                       // single metric
    .BuildAggregate();

var response = await searchClient.AggregateAsync(request);
foreach (var result in response.Results)
{
    if (result.Buckets.Count > 0)
        foreach (var b in result.Buckets)
            Console.WriteLine($"{result.Name}: {b.Key} = {b.Count}");
    else
        Console.WriteLine($"{result.Name} = {result.MetricValue}");
}
```

Calendar intervals: `minute`, `hour`, `day`, `week`, `month`, `quarter`, `year`. Range bounds are half-open (`from` inclusive, `to` exclusive); `null` means unbounded.

Other languages construct `AggregateRequest` directly from generated protos — the fields mirror the builder one-for-one (`aggregations[]` with `type`, `field`, `size`, `calendar_interval`, `range_buckets`, plus `having` and `joins`).

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
using Iverson.Client.Search;
using static Iverson.Client.Search.SearchOperators;

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
import io.iverson.client.search.Query;
import iverson.ObjectSearch.SearchOperator;

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
from iverson_client import pipeline
from iverson_client.search import SearchOperator

request = (
    pipeline("Article")
    .where("IsPublished", SearchOperator.EQUALS, True)
    .step("by_author", lambda s: s
          .group_by("AuthorId")
          .count_all("articles")
          .having("articles", SearchOperator.GREATER_THAN, 5))
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
import { pipeline, SearchOperator } from '@iverson/client';

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

---

## Semantic search: Qdrant surfaces

### How content gets vectorized

Annotate properties at schema registration — every language has an equivalent mechanism (C# attributes, Java/TypeScript decorators, Python field metadata, Go struct tags); schemas registered once serve every client:

```csharp
public class Article
{
    [IversonEmbedding]                          // whole-field vector → entity collection
    public string Title { get; set; } = "";

    [IversonChunk(maxTokens: 512, overlap: 64)] // windowed vectors → {collection}_chunks
    public string Body { get; set; } = "";
}
```

The server embeds at ingestion time using its configured model (Ollama `nomic-embed-text`, 768 dimensions, in the docker-compose stack) — and embeds your *query text* with the same model at search time. No API keys, no client-side models, no drift between ingest and query.

`[IversonChunk]` splits long fields into overlapping windows (≈4 chars per token) so relevant content near a window boundary is still retrievable, and stores each window as its own point with `text` and `parent_id` payload.

### SearchSimilar — entity-level semantic search

Send raw text; get back whole entities ranked by vector similarity:

```csharp
var stream = searchClient.SearchSimilar(new SearchSimilarRequest
{
    TypeName = "Article",
    Property = "Title",              // the [IversonEmbedding] property
    Query    = "the crossover that changed basketball",
    TopK     = 5
});

await foreach (var r in stream.ResponseStream.ReadAllAsync())
    Console.WriteLine($"[{r.Score:F4}] {r.Data}");
```

```python
from iverson_client.generated import object_search_pb2 as search_pb

request = search_pb.SearchSimilarRequest(
    type_name="Article", property="Title",
    query="the crossover that changed basketball", top_k=5)

for r in search.SearchSimilar(request):
    print(f"[{r.score:.4f}] {r.data}")
```

Unlike `Search`, these scores are real: Qdrant ANN similarity, higher is closer.

### SearchChunks — passage retrieval for RAG

`SearchChunks` returns the most relevant *passages*, not documents — exactly what an LLM prompt wants:

```typescript
const stream = search.searchChunks({
    typeName: 'Article',
    property: 'Body',                 // the [IversonChunk] property
    query: 'how did Iverson change defensive rules in the NBA?',
    topK: 3,
    traceId: '',
});

const passages: string[] = [];
for await (const chunk of stream) {
    passages.push(chunk.chunkText);   // also: chunk.parentKey, chunk.score
}

const prompt = `Answer using only the passages below.\n\n` +
    passages.map((t, i) => `[${i + 1}] ${t}`).join('\n\n') +
    `\n\nQuestion: How did Iverson change defensive rules in the NBA?`;
```

Each hit carries `parentKey` — feed it to your coordinator's `get` to pull the full document alongside the passage.

> **Heads-up:** the query builders still expose a `vectorSimilar` clause (a pre-computed `float[]` inside a structured query). The StarRocks `Search` path **ignores** these clauses today — use `SearchSimilar` / `SearchChunks` for vector work.

---

## Capability matrix

| | C# | Java | Python | TypeScript | Go |
|---|---|---|---|---|---|
| Typed property expressions | ✅ | — | — | — | — |
| Core operators (eq, neq, gt/gte/lt/lte, contains, in) | ✅ | ✅ | ✅ | ✅ | ✅ |
| `startsWith` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `endsWith` | ✅ ¹ | ✅ | ✅ | ✅ | ✅ |
| Field projection (`fields`) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Joins on `Search` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `GroupByBuilder` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `PipelineBuilder` (CTE chains) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Aggregate DSL builder | ✅ | proto ² | proto ² | proto ² | proto ² |
| Typed search execution helper | `SearchAsync` | `search()` | raw stub | raw stub | raw stub |
| Embedding/chunk schema annotations | ✅ | ✅ | ✅ | ✅ | ✅ |

¹ Via `SearchOperator.EndsWith`; no `SearchOperators` static alias yet.
² Construct `AggregateRequest` from generated protos.

## Gotchas worth knowing

- **`Search` scores are constant (`1.0`).** StarRocks has no relevance concept; order results with `orderBy`. Real scores come from `SearchSimilar` / `SearchChunks`.
- **Dates are ISO 8601 strings** on the wire; `IN` lists are strings.
- **Field names are case-insensitive**; with joins, qualify ambiguous fields as `TypeName.FieldName`.
- **Go's `GroupByBuilder.Where`/`Having` take raw `*pb.SearchValue`**, unlike its `QueryBuilder` which converts scalars for you.
- **StarRocks `STRING` columns cap at 64 KB.** Oversized text belongs in `[IversonLargeField]` / chunked properties, not filterable columns.
