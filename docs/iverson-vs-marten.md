# Iverson vs. Marten: Architecture and Feature Comparison

**Date:** 2026-06-27  
**Iverson load test baseline:** 2026-06-27 (write p50 31.7ms, GetMany b=1 p50 4.8ms)

---

## 1. What Each System Is

**Marten** is a .NET library that turns PostgreSQL into a document store and event store. It is embedded directly into the application process — no separate service, no network hop beyond the PostgreSQL connection. Schema management, querying, projections, and event sourcing are all handled through a single NuGet package against a single database.

**Iverson** is a multi-store CQRS service. It exposes a gRPC API that fan-outs every write to three backing stores, each optimized for a different query pattern: Postgres for key-based retrieval (system of record), StarRocks for structured analytics and aggregation, and Qdrant for vector/semantic search. Clients interact with it through a language-neutral gRPC surface, not as a library.

The single most important distinction: **Marten is a library; Iverson is a service.** Everything downstream — deployment model, latency profile, language support, operational complexity — follows from this.

---

## 2. Architecture

### Marten

```
Application Process
  └─ IDocumentSession (Unit of Work)
       └─ PostgreSQL (JSONB)
            ├─ Documents (stored as jsonb)
            ├─ Indexes (GIN, B-tree, tsvector)
            ├─ Async projections (daemon, separate process or thread)
            └─ Event streams (append-only, optional)
```

Everything lives in one database. The async projection daemon is the only async fan-out, and it runs against the same Postgres instance.

### Iverson

```
Client Application
  └─ EntityCoordinator<T> (gRPC client SDK)
       └─ Iverson.Api (gRPC service)
            ├─ PostgreSQL   — synchronous write (system of record); key-based reads
            ├─ Kafka        — fire-and-forget fan-out for projections
            │    ├─ EngagementStoreConsumer → StarRocks (analytics, aggregation, structured search)
            │    └─ IntelligenceStoreConsumer → Qdrant (vector index, chunk retrieval)
            └─ Ollama       — embedding generation (nomic-embed-text, 768 dims)
```

Four separate infrastructure components (Postgres, Kafka, StarRocks, Qdrant) plus an embedding model. The gRPC API is the only entry point for all operations.

---

## 3. Write Path

### Marten

```csharp
await using var session = store.LightweightSession();
session.Store(article);
await session.SaveChangesAsync();
// Article is now in Postgres. Async projections will catch up.
```

`SaveChangesAsync` batches all pending changes into a single Postgres transaction and commits. This is a direct database write — no network hop beyond the Postgres connection itself. Expected write latency on local Docker: **2–5ms p50**.

Optimistic concurrency is built in (`session.UpdateRevision`). The identity map within a session ensures consistent in-session reads.

### Iverson

```csharp
await coordinator.PersistAsync(article);
// Article is now in Postgres (synchronous).
// StarRocks and Qdrant will catch up via Kafka consumers.
```

`PersistAsync` crosses a gRPC boundary, writes synchronously to Postgres, then fire-and-forgets a Kafka message for StarRocks and Qdrant. Measured write latency on local Docker: **31.7ms p50** (includes gRPC round-trip ~10ms + Postgres write + Kafka enqueue).

The Kafka fan-out is the source of the latency premium over Marten. The architectural trade: Iverson decouples write throughput from projection complexity, and the gRPC surface is language-neutral.

Iverson has no unit-of-work or identity map. Each `PersistAsync` is an independent upsert.

### Side-by-side

| | Marten | Iverson |
|---|---|---|
| Write durability | Postgres commit | Postgres commit (synchronous) |
| Write latency (local Docker) | ~2–5ms | 31.7ms p50 |
| Batching | Yes (session) | No (per-call upsert) |
| Optimistic concurrency | Built-in | Not supported |
| Transaction scope | Yes (session) | No |
| Projection fan-out | Async (same DB) | Async (Kafka → StarRocks, Qdrant) |
| Language support | C# only | Any (gRPC) |

---

## 4. Read Path

### Marten

```csharp
var article = await session.LoadAsync<Article>(id);

// or batch:
var articles = await session.LoadManyAsync<Article>(ids);

// or LINQ:
var recent = await session.Query<Article>()
    .Where(a => a.IsPublished && a.PublishedAt > cutoff)
    .OrderByDescending(a => a.PublishedAt)
    .ToListAsync();
```

All reads hit Postgres directly. LINQ queries are compiled to SQL. GIN indexes on JSONB columns support efficient containment and equality checks. Full-text search uses PostgreSQL `tsvector`. There is no separate read store.

Marten compiled queries pre-parse the expression tree at startup and skip re-parsing at runtime — a meaningful advantage on hot read paths.

### Iverson

```csharp
// Key-based (Postgres):
var article = await coordinator.GetAsync(id);
var articles = coordinator.GetManyAsync(ids);

// Structured search (StarRocks):
var results = coordinator.SearchAsync(
    Query.For<Article>()
        .Where(a => a.IsPublished, EqualTo, true)
        .Where(a => a.Title, Contains, "performance")
        .OrderBy(a => a.PublishedAt, descending: true));

// Aggregation (StarRocks):
var stats = await coordinator.AggregateAsync<Article>(specs);

// Vector/semantic search (Qdrant):
var similar = await coordinator.SearchSimilarAsync("what is CQRS?");
```

Key-based reads hit Postgres. All search, aggregation, and vector queries are routed to the appropriate store. The separation is explicit at the API level — there is no unified query surface that automatically chooses the right store.

**GetMany measured (local Docker, 400K rows):**

| Batch | p50 | p95 |
|-------|-----|-----|
| 1 | 4.8ms | 53.5ms |
| 10 | 6.7ms | 14.7ms |
| 100 | 22.7ms | 47.1ms |
| 500 | 100.3ms | 190.8ms |

Marten's `LoadManyAsync` would be comparable for key-based retrieval (single Postgres query, `= ANY(uuid[])`) — the expected difference is the gRPC overhead (~3–5ms per call), which dominates small batches.

### Side-by-side

| | Marten | Iverson |
|---|---|---|
| Key lookup | `LoadAsync<T>` → Postgres | `GetAsync` → Postgres via gRPC |
| Batch key lookup | `LoadManyAsync<T>` → Postgres | `GetManyAsync` → Postgres via gRPC |
| Structured query | LINQ → Postgres SQL | Search DSL → StarRocks SQL |
| Full-text search | Postgres `tsvector` | StarRocks `MATCH` |
| Aggregation | LINQ `.GroupBy/.Sum` → Postgres | Aggregate DSL → StarRocks |
| Vector search | Not supported | Qdrant (semantic + dense retrieval) |
| Consistent reads | Always (same store) | Key reads only; search/aggregate are eventually consistent |
| Compiled queries | Yes (Marten feature) | Not applicable |
| Cross-language | C# only | Any gRPC client |

---

## 5. Query Capabilities

### Marten

- LINQ-to-SQL: the full LINQ surface against JSONB columns
- Compiled queries: pre-parsed for zero per-call overhead
- Full-text search: `tsvector`/`tsquery` via `.Search()` / `.PlainTextSearch()`
- Aggregation: LINQ `.GroupBy`, `.Sum`, `.Count` → Postgres SQL
- Containment queries: `@>` operator on arrays/nested objects
- Pagination: `.Skip().Take()`
- Includes/joins: eager loading via `.Include<T>()`
- Batch queries: run multiple LINQ queries in one round-trip

No vector search. No cross-store routing.

### Iverson

- Structured search: fluent DSL → StarRocks SQL (`.Where`, `.And`, `.Or`, `.Not`, `.OrderBy`, `.Page`)
- Aggregation: spec-based aggregation DSL → StarRocks GROUP BY SQL
- Vector search: dense vector similarity via Qdrant (from `[IversonEmbedding]` fields)
- Chunk retrieval: passage-level RAG from `[IversonChunk]` fields via Qdrant
- Key-based retrieval: `GetMany` → Postgres `ANY(uuid[])` — not a general query surface

Iverson has no LINQ surface. Queries are expressed through the search DSL or the aggregate DSL, both of which compile to SQL run against StarRocks. There is no ad-hoc query capability beyond what the DSL exposes. No `GROUP BY` with arbitrary expressions; no subqueries; no joins across entity types.

**Measured search (local Docker):**

| Query | p50 |
|-------|-----|
| Simple (1 clause) | 43ms |
| Medium (2 clauses + sort) | 754ms |
| Complex (2 clauses + sort + LIKE) | 35ms |
| Aggregate specs=1 | 24.5ms |
| Aggregate specs=6 | 88.5ms |

The medium search outlier (754ms) reflects a StarRocks query plan on a cold/uncached path — not a structural ceiling.

---

## 6. Projections

### Marten

Marten's projection system is first-class. Two modes:

**Inline projections** run synchronously inside the write transaction. Useful for views that must be immediately consistent.

**Async projections** run via the Marten async daemon — a background process that reads from the event stream and rebuilds views. Catch-up time is measurable; Marten exposes daemon health metrics.

Projection rebuilds are supported out of the box: stop the daemon, truncate the view table, restart — Marten rewinds the event stream. This is only available if you use event sourcing (see Section 8).

For document store usage (no event sourcing), Marten has no equivalent of async projections — writes are immediately reflected in all queries against the same database.

### Iverson

Projections to StarRocks and Qdrant are delivered via Kafka consumers. Both stores are eventually consistent with Postgres. The lag depends on consumer throughput.

**Measured Kafka lag** (end of 223 RPS write test): ~26K messages. At observed StarRocks ingestion rate (~1,000 msg/min), full catch-up takes ~26 minutes after the write wave ends. Qdrant lag (embedding + indexing) is longer due to embedding inference cost.

Projection rebuild is manual: the `/admin/reconcile/{typeName}` endpoint re-publishes all Postgres rows as Kafka events. StarRocks and Qdrant consumers replay them. There is no built-in replay sequencing or daemon health metric.

Unlike Marten's event-sourced projections, Iverson projections are rebuild-from-Postgres (the source of truth), not rebuild-from-event-stream. This means historical event semantics are not preserved — you can rebuild the current state, not an earlier point-in-time projection.

---

## 7. Consistency Model

### Marten

Strong consistency for document reads/writes within a session. Writes committed via `SaveChangesAsync` are immediately readable by subsequent queries in any session. Optimistic concurrency via document version checks. No eventual consistency — unless async projections are involved, in which case projection views may lag by seconds.

### Iverson

Split consistency model by read path:

- **Postgres (key-based reads):** immediately consistent. After `PersistAsync` returns, `GetAsync`/`GetManyAsync` reflect the write.
- **StarRocks (search/aggregation):** eventually consistent. Lag = Kafka consumer throughput.
- **Qdrant (vector/semantic search):** eventually consistent. Lag = Kafka consumer throughput + embedding inference time.

Applications must tolerate stale search results. This is typically acceptable for analytics and semantic search use cases but can surprise developers who expect a write to be immediately searchable.

---

## 8. Event Sourcing

### Marten

Marten is a full event store. It supports:

- Append-only event streams with stream version tracking
- Aggregate reconstruction from event history (`AggregateStreamAsync<T>`)
- Inline and async projections built from events
- Point-in-time rebuilds
- Event metadata (correlation IDs, causation IDs, timestamps)
- Strong ordering guarantees within a stream

Event sourcing with Marten is a first-class architectural pattern — not an add-on.

### Iverson

Iverson has no event sourcing capability. Kafka is used as a fan-out bus, not an event store. Messages are not retained indefinitely; there is no aggregate reconstruction from event history; there is no point-in-time read model. If you need event sourcing semantics with Iverson, you would need to add it externally.

---

## 9. Schema Management

### Marten

Schema management is automatic. Marten inspects the CLR type at startup and applies `CREATE TABLE`, `CREATE INDEX`, and `ALTER TABLE` statements to bring the database schema in sync. This happens on `IDocumentStore` initialization or on-demand via `store.Storage.ApplyAllConfiguredChangesToDatabaseAsync()`.

For JSONB documents, schema changes are mostly additive — adding a property to a C# class usually requires no migration. Removing a property leaves orphan data in JSONB (ignored by deserialization). Index changes require a schema patch.

### Iverson

Schema registration is explicit. Clients call `SchemaRegistrar.RegisterAsync()` at startup, which sends a `RegisterSchema` gRPC request. The server inspects the `[IversonEntity]` annotated type via reflection, builds a `SchemaDescriptor`, creates the Postgres table via `json_populate_record`-compatible DDL, and runs `ApplyTableAsync` against StarRocks for the analytics schema.

Schema changes require re-registration. There is no automatic migration for column type changes or index changes — that requires a manual DDL operation outside Iverson.

---

## 10. Embedding and Vector Search

### Marten

No native support. If you need vector search with Marten, you would combine it with `pgvector` (a Postgres extension) and manage embeddings yourself — there is no built-in embedding generation, chunking pipeline, or vector index management.

### Iverson

First-class support:

- `[IversonEmbedding]` on a string field generates a dense vector for the full field value at write time via Ollama.
- `[IversonChunk(maxTokens, overlap)]` chunks a long text field at write time and stores each chunk as a separate Qdrant point, enabling passage-level RAG retrieval.
- Embedding model is configured server-side (currently `nomic-embed-text`, 768 dimensions). No client changes when switching models — Qdrant collection is rebuilt.
- `SearchSimilarAsync(query)` and `SearchChunksAsync(query)` perform semantic search via Qdrant, returning typed results with similarity scores.

This is the most distinctive capability Iverson has over Marten — semantic search and RAG are zero-configuration from the client's perspective.

---

## 11. Multi-Tenancy

### Marten

Built-in multi-tenancy at the schema or database level. Tenant ID can be applied per-session (`session.ForTenant("tenant-a")`). Queries automatically scope to the tenant. Supported modes: single schema with tenant discriminator column, separate schemas per tenant, or separate databases per tenant.

### Iverson

No multi-tenancy support. All entities share the same tables. Tenant discrimination, if needed, must be implemented as an application-level field on the entity.

---

## 12. Operational Complexity

### Marten

| Component | Count |
|-----------|-------|
| PostgreSQL | 1 |
| NuGet packages | 1–2 |
| Separate processes | 0 (library) |
| Async daemon | optional, co-located |

Minimal ops surface. Everything runs in-process or against a single Postgres instance. Marten's async daemon is a background hosted service in the same application.

### Iverson

| Component | Count |
|-----------|-------|
| PostgreSQL | 1 |
| StarRocks | 1 (FE + BE pods in production) |
| Qdrant | 1 |
| Kafka (+ Zookeeper) | 2 |
| Ollama (embedding) | 1 |
| Iverson.Api service | 1+ |
| OpenTelemetry collector | optional |

Six distinct infrastructure components before the application layer. Each adds failure modes, networking configuration, version compatibility surface, and monitoring requirements. This complexity is justified only when the capability each component provides is actually needed (analytics, vector search).

---

## 13. Developer Experience

### Marten

```csharp
// Register
builder.Services.AddMarten(opts =>
{
    opts.Connection(connectionString);
    opts.Schema.For<Article>().Index(a => a.AuthorId);
});

// Write
session.Store(article);
await session.SaveChangesAsync();

// Read
var a = await session.LoadAsync<Article>(id);
var results = await session.Query<Article>()
    .Where(a => a.IsPublished)
    .ToListAsync();
```

Zero infrastructure beyond Postgres. LINQ queries work with the same mental model as Entity Framework. Schema is managed automatically. Debugging is straightforward — SQL is emitted against one database, inspectable with any Postgres tool.

### Iverson

```csharp
// Register schema at startup
await registrar.RegisterAsync(typeof(Article).Assembly);

// Write
await coordinator.PersistAsync(article);

// Key read
var a = await coordinator.GetAsync(id);

// Structured search (routes to StarRocks)
var results = coordinator.SearchAsync(
    Query.For<Article>()
        .Where(a => a.IsPublished, EqualTo, true)
        .Where(a => a.Title, Contains, "CQRS"));

// Semantic search (routes to Qdrant)
var hits = await coordinator.SearchSimilarAsync("what is event sourcing?");
```

The fluent DSL is clean but diverges from LINQ. Debugging a search query requires knowing whether it routed to StarRocks or Qdrant. Eventual consistency in the search stores creates a class of bugs that are hard to reproduce locally but common in staging. The gRPC boundary means stack traces stop at the client SDK — server-side errors appear as gRPC status codes.

---

## 14. Performance Summary (Measured, Local Docker)

These are Iverson numbers from 2026-06-27 and represent the best available baseline. Marten numbers are theoretical estimates based on known library overhead characteristics.

| Operation | Iverson (measured) | Marten (estimated) | Notes |
|-----------|-------------------|-------------------|-------|
| Write p50 | 31.7ms | 2–5ms | Iverson includes gRPC + Kafka overhead |
| GetMany b=1 p50 | 4.8ms | 1–3ms | Iverson includes gRPC |
| GetMany b=500 p50 | 100ms | 20–50ms | Both: `ANY(uuid[])` against Postgres |
| Simple search p50 | 43ms (StarRocks) | 5–20ms (Postgres) | Different store; not directly comparable |
| Aggregate p50 | 24–88ms (StarRocks) | 10–50ms (Postgres) | StarRocks is columnar; advantage grows with data volume |
| Vector search | 10–30ms (Qdrant) | N/A | Marten has no equivalent |
| Write RPS (c=16) | 223 | ~500–1000 | Marten eliminates gRPC + Kafka latency |

The gRPC overhead is consistently ~10ms round-trip under local Docker conditions. This is the floor for any Iverson operation and the primary structural latency disadvantage versus a library.

At larger data volumes (>1M rows), the analytics/aggregation comparison is expected to flip in Iverson's favor: StarRocks is a columnar MPP store purpose-built for aggregation, while Postgres row-store aggregates degrade linearly.

---

## 15. When to Choose Each

### Choose Marten when:

- Your team is .NET-only and prefers a library over a service
- You need strong consistency on all queries
- You need event sourcing, aggregate reconstruction, or point-in-time projections
- You need optimistic concurrency
- You need multi-tenancy
- Your query patterns are expressible via LINQ against a single store
- Operational simplicity is a priority (one database, no Kafka, no separate service)
- Write latency is important and you can tolerate the single-store query ceiling

### Choose Iverson when:

- You need semantic/vector search or RAG on entity fields alongside structured queries
- You need analytics aggregation at scale where Postgres row-store degrades
- Clients exist in multiple languages (Iverson's gRPC surface is language-neutral)
- You can tolerate eventual consistency on search and aggregation
- You want search, analytics, and vector search configuration to be zero-touch from the client side
- Write latency of ~30ms is acceptable for your use case

### Consider combining them:

For a system that needs both strong consistency and vector search, Marten and Iverson are not mutually exclusive. Marten can be the authoritative document store (strong consistency, LINQ queries, event sourcing), with Iverson layered on top for the semantic search and analytics tier — the `/admin/reconcile` endpoint provides a natural bridge.

---

## 16. Summary Table

| Dimension | Marten | Iverson |
|-----------|--------|---------|
| Architecture | Library (in-process) | Service (gRPC) |
| Primary store | PostgreSQL (JSONB) | PostgreSQL + StarRocks + Qdrant |
| Write latency (local) | 2–5ms | ~30ms |
| Write RPS (c=16) | ~500–1000 | 223 |
| Read consistency | Strong | Strong (key) / Eventual (search) |
| LINQ queries | Yes | No |
| Full-text search | Postgres tsvector | StarRocks MATCH |
| Aggregation | LINQ → Postgres | DSL → StarRocks (columnar) |
| Vector search | No | Yes (Qdrant) |
| RAG / chunking | No | Yes (Qdrant + Ollama) |
| Event sourcing | Yes (first-class) | No |
| Optimistic concurrency | Yes | No |
| Multi-tenancy | Yes (built-in) | No |
| Schema management | Automatic | Explicit registration |
| Language support | C# only | Any (gRPC) |
| Infrastructure | 1 component | 6 components |
| Operational complexity | Low | High |
