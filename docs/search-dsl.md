# Iverson Search DSL

The search DSL is a fluent C# API that compiles to a `SearchRequest` protobuf message and is routed by `ObjectSearchGrpcService` to the appropriate backend store. Three separate query surfaces exist depending on whether you want structured field queries, server-side semantic search, or passage-level RAG retrieval.

---

## Quick-start

```csharp
using Iverson.Client.Search;
using static Iverson.Client.Search.SearchOperators;

var results = articles.SearchAsync(
    Query.For<Article>()
        .Where(a => a.IsPublished, EqualTo, true)
        .And(a  => a.Title,       Contains, "Iverson")
        .OrderBy(a => a.PublishedAt, descending: true)
        .Page(1, size: 10));

await foreach (var r in results)
    Console.WriteLine($"[{r.Score:F2}] {r.Entity.Title}");
```

Import `using static Iverson.Client.Search.SearchOperators;` to use operator names without the `SearchOperator.` prefix.

---

## 1. Structured search — `Search`

### Entry point

```csharp
Query.For<T>()        // scopes the query to a registered entity type
    ...               // add clauses
    .Build()          // → SearchRequest proto (sent by EntityCoordinator)
```

`EntityCoordinator<T>.SearchAsync(QueryBuilder<T>)` sends the compiled request to `ObjectSearchGrpcService.Search`, which routes it to Elasticsearch.

---

### Clause types

Every clause takes a strongly-typed property expression, an operator, and a value. Clause type controls how the clause participates in Elasticsearch's Boolean query.

| Method | ES role | Effect |
|--------|---------|--------|
| `.Where(p, op, val)` | `filter` | Must match; **does not affect relevance score** |
| `.And(p, op, val)` | `must` | Must match; **contributes to score** |
| `.Or(p, op, val)` | `should` | Optional; **boosts score when matched** |
| `.Not(p, op, val)` | `must_not` | Must not match; excluded from results |

```csharp
Query.For<Article>()
    .Where(a => a.IsPublished, EqualTo, true)          // hard filter, no score cost
    .And(a  => a.Title,       Contains, "crossover")   // scored full-text match
    .Or(a   => a.Body,        Contains, "Philadelphia") // optional boost
    .Not(a  => a.AuthorId,   EqualTo,  bannedAuthorId)
```

---

### Operators

| Constant | Alias | Value type | ES mapping |
|----------|-------|------------|------------|
| `EqualTo` | `Equals`¹ | string, number, bool, DateTime | `term` |
| `NotEquals` | | same | `must_not term` |
| `Contains` | | string | `match` (analyzed) |
| `StartsWith` | | string | `prefix` |
| `GreaterThan` | | number, DateTime | `range.gt` |
| `LessThan` | | number, DateTime | `range.lt` |
| `GreaterThanOrEquals` | | number, DateTime | `range.gte` |
| `LessThanOrEquals` | | number, DateTime | `range.lte` |
| `In` | | `IEnumerable<string>` | `terms` |
| `VectorSimilar` | | `float[]` | Qdrant ANN / ES kNN |

¹ `Equals` is available but prefer `EqualTo` to avoid shadowing `object.Equals`.

**DateTime values** are serialized as ISO 8601 strings (`"o"` format) and matched against Elasticsearch `date` fields.

```csharp
// Date range
Query.For<Article>()
    .Where(a => a.PublishedAt, GreaterThan,       DateTime.UtcNow.AddDays(-30))
    .Where(a => a.PublishedAt, LessThanOrEquals,  DateTime.UtcNow)

// IN — match any of a set of values
var tagIds = new[] { "tag-uuid-1", "tag-uuid-2" };
Query.For<Article>()
    .Where(a => a.AuthorId, In, tagIds)
```

---

### Sorting

```csharp
Query.For<Article>()
    .OrderBy(a => a.PublishedAt, descending: true)   // primary sort
    .OrderBy(a => a.Title)                            // secondary sort (ascending)
```

Multiple `.OrderBy` calls stack in the order they are added.

---

### Paging

```csharp
Query.For<Article>()
    .Page(page: 2, size: 25)   // defaults: page=1, size=20
```

---

### Top-level clause logic

By default, all clauses are evaluated independently within their type bucket (`must`, `should`, etc.). `.WithLogic` changes how the server interprets top-level `should` clauses:

```csharp
// OR semantics: any tag slug matches
Query.For<Tag>()
    .Or(t => t.Slug, Contains, "basketball")
    .Or(t => t.Slug, Contains, "culture")
    .Or(t => t.Slug, Contains, "nba")
    .WithLogic(SearchLogic.Or)
    .OrderBy(t => t.Label)
```

| Value | Behaviour |
|-------|-----------|
| `SearchLogic.And` | All `must` clauses required; `should` boosts (default) |
| `SearchLogic.Or` | At least one top-level clause must match |

---

### Vector similarity within a structured query

When you already have a pre-computed embedding, you can include it as a clause. Use the dedicated `*VectorSimilar` overloads — the property expression targets the annotated `string` property, but the value is a `float[]`.

| Method | ES role |
|--------|---------|
| `.WhereVectorSimilar(p, float[])` | filter |
| `.AndVectorSimilar(p, float[])` | must |
| `.OrVectorSimilar(p, float[])` | should |
| `.NotVectorSimilar(p, float[])` | must_not |

```csharp
float[] precomputedEmbedding = myLocalModel.Encode("point guard era");

Query.For<Article>()
    .Where(a => a.IsPublished, EqualTo, true)
    .OrVectorSimilar(a => a.Title, precomputedEmbedding)
    .Page(1, size: 5)
```

> **When to use this vs `SearchSimilar`:** Use `*VectorSimilar` when you have already computed the query embedding on the client (e.g. from a local model). Use `SearchSimilar` (below) when you want the server to embed a raw text query using the same model that was used during ingestion.

---

## 2. Server-side semantic search — `SearchSimilar`

`SearchSimilar` accepts a plain text query. The server embeds it using the same `EmbeddingModel` declared on the `[IversonEmbedding]` property and searches the entity's Qdrant named-vector collection.

**Requirement:** the target property must be annotated with `[IversonEmbedding]`.

```csharp
var request = new SearchSimilarRequest
{
    TypeName = "Article",
    Property = "Title",            // [IversonEmbedding(EmbeddingModel.OpenAiTextEmbedding3Small)]
    Query    = "point guard era",  // embedded server-side
    TopK     = 10,
    TraceId  = traceId
};

var stream = searchClient.SearchSimilar(request);
await foreach (var r in stream.ResponseStream.ReadAllAsync())
    Console.WriteLine($"[{r.Score:F4}] {r.Data}");
```

Returns `SearchResponse` (same type as `Search`): `{ Data: Struct, Score: float, TraceId }`.

### How it works

1. Server looks up the `VectorDescriptor` for the requested property.
2. Calls `IEmbeddingService.EmbedAsync(query, vectorDesc.ModelId)`.
3. Searches Qdrant collection `{tableName}` using the named vector `{property_snake_case}_vector`.
4. Returns Qdrant payload fields as proto `Struct` values with ANN scores.

---

## 3. Passage retrieval (RAG) — `SearchChunks`

`SearchChunks` targets properties annotated with `[IversonChunk]`. At ingestion time the server split each field value into overlapping windows, embedded each window, and stored it in a separate `{collection}_chunks` Qdrant collection with `text` and `parent_id` in the payload.

`SearchChunks` retrieves the most relevant passages — not full documents — making it the correct entry point for building RAG context.

**Requirement:** the target property must be annotated with `[IversonChunk]`.

```csharp
var request = new SearchChunksRequest
{
    TypeName = "Article",
    Property = "Body",              // [IversonChunk(EmbeddingModel.OpenAiTextEmbedding3Small, maxTokens: 512, overlap: 64)]
    Query    = "crossover dribble", // embedded server-side
    TopK     = 5,
    TraceId  = traceId
};

var stream = searchClient.SearchChunks(request);
await foreach (var r in stream.ResponseStream.ReadAllAsync())
{
    Console.WriteLine($"Parent: {r.ParentKey}");
    Console.WriteLine($"Score:  {r.Score:F4}");
    Console.WriteLine($"Text:   {r.ChunkText}");
}
```

Returns `ChunkSearchResponse`: `{ ParentKey: string, ChunkText: string, Score: float, TraceId }`.

Use `ParentKey` to fetch the full entity with `EntityCoordinator<T>.GetAsync(parentKey)` if you need the surrounding document alongside the retrieved passage.

### How it works

1. Server looks up the `ChunkDescriptor` for the requested property.
2. Calls `IEmbeddingService.EmbedAsync(query, chunkDesc.ModelId)`.
3. Searches Qdrant collection `{tableName}_chunks` using named vector `{property_snake_case}_vector`.
4. Each Qdrant point carries payload `{ text, parent_id, field, chunk_index }`.
5. Returns `text` and `parent_id` per hit.

### Chunking parameters

Chunking behaviour is controlled by the attribute on the model:

```csharp
[IversonChunk(
    EmbeddingModel.OpenAiTextEmbedding3Small,
    maxTokens: 512,    // approximate window size (1 token ≈ 4 chars)
    overlap: 64)]      // tokens shared between adjacent windows
public string Body { get; set; } = string.Empty;
```

Overlapping windows ensure that relevant content near a window boundary is still retrievable.

---

## Routing summary

| Surface | Client API | Who embeds | Backend |
|---------|-----------|------------|---------|
| `Search` — scalar/text | `QueryBuilder<T>` | client (no embedding) | Elasticsearch |
| `Search` — `*VectorSimilar` | `QueryBuilder<T>` | **client** (pre-computed `float[]`) | Elasticsearch / Qdrant |
| `SearchSimilar` | `SearchSimilarRequest` | **server** | Qdrant (entity collection) |
| `SearchChunks` | `SearchChunksRequest` | **server** | Qdrant (`_chunks` collection) |

---

## Full examples

### Text search with filters and sorting

```csharp
var query = Query.For<Article>()
    .Where(a => a.IsPublished, EqualTo,      true)
    .Where(a => a.PublishedAt, GreaterThan,  DateTime.UtcNow.AddDays(-90))
    .And(a  => a.Title,       Contains,     "AI")
    .Or(a   => a.Body,        Contains,     "machine learning")
    .OrderBy(a => a.PublishedAt, descending: true)
    .Page(1, size: 20);

await foreach (var r in articles.SearchAsync(query))
    Console.WriteLine($"[{r.Score:F2}] {r.Entity.Title}");
```

### Multi-field OR across tags

```csharp
var query = Query.For<Tag>()
    .Or(t => t.Slug, Contains, "basketball")
    .Or(t => t.Slug, Contains, "nba")
    .Or(t => t.Slug, Contains, "sixers")
    .WithLogic(SearchLogic.Or)
    .OrderBy(t => t.Label);

await foreach (var r in tags.SearchAsync(query))
    Console.WriteLine(r.Entity.Label);
```

### Server-side semantic entity search

```csharp
// No embedding needed on the client — the server uses the same model
// as [IversonEmbedding] on Article.Title
var request = new SearchSimilarRequest
{
    TypeName = "Article",
    Property = "Title",
    Query    = "the crossover that changed basketball",
    TopK     = 5
};

await foreach (var r in searchClient.SearchSimilar(request).ResponseStream.ReadAllAsync())
    Console.WriteLine($"[{r.Score:F4}] {r.Data.Fields["Title"].StringValue}");
```

### RAG passage retrieval

```csharp
// Retrieve the most relevant passages from Article.Body,
// then use them as context for an LLM prompt
var request = new SearchChunksRequest
{
    TypeName = "Article",
    Property = "Body",
    Query    = "how did Iverson change defensive rules in the NBA?",
    TopK     = 3
};

var context = new List<string>();
await foreach (var r in searchClient.SearchChunks(request).ResponseStream.ReadAllAsync())
    context.Add(r.ChunkText);

var prompt = $"""
    Answer the question using only the passages below.

    Passages:
    {string.Join("\n\n", context.Select((t, i) => $"[{i + 1}] {t}"))}

    Question: How did Iverson change defensive rules in the NBA?
    """;
```
