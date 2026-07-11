# Iverson

**Write once. Query every way. No plumbing.**

Every serious application eventually needs the same three things from its data: transactional truth, fast analytics, and semantic search. The usual answer is three databases, three ingestion pipelines, three schemas drifting apart, and a standing meeting about why they disagree.

Iverson's answer: **one write, one schema, one query model** — and the polyglot persistence problem becomes the platform's job instead of yours.

- **One write fans out to every store.** A single gRPC call commits to PostgreSQL, then an event-driven pipeline projects it into StarRocks and Qdrant automatically. You never write projection code.
- **One schema registration provisions everything.** Register a type once — tables, columns, vector collections, and text-chunking pipelines are created in every store, from the same definition, in every language.
- **One query DSL routes itself.** Filters, joins, and aggregations compile to StarRocks SQL; "find things *about* this" compiles to Qdrant vector search — with embeddings computed server-side, so your client never touches a model. Same fluent builder in C#, Java, Python, TypeScript, and Go.
- **One trace ID follows the whole journey.** OpenTelemetry instrumentation end to end: the gRPC call, the Kafka hop, and each store's projection share a single trace in Jaeger — with Prometheus metrics and backlog-depth gauges alongside it.

> The name honors the original AI — Allen Iverson. Maximum efficiency, minimal resources, zero regard for conventional wisdom. Same philosophy here.

---

## The architecture in one paragraph

A gRPC API validates each write and commits it to **PostgreSQL** — the system of record — then publishes it to a single Kafka topic, keyed by entity, so create/update/delete for the same row always land in order. Independent consumers project it into **StarRocks** (columnar analytics: filters, joins, GroupBy, facets over hundreds of millions of rows) and **Qdrant** (vector search: semantic similarity and passage-level RAG retrieval, embedded locally via **Ollama** — no API keys, no data leaving the host). The API and its projection consumers scale independently: the same image runs as `api` (gRPC/HTTP) or `worker` (Kafka consumers and background jobs), selected by one environment variable, and deployed as separate Kubernetes workloads. Stores are eventually consistent; PostgreSQL is authoritative — a reconcile endpoint replays it through the pipeline to rebuild any store from scratch, and anything the pipeline can't project lands in a dead-letter queue with admin list/replay endpoints.

| Store | Job | Why it's there |
|-------|-----|----------------|
| PostgreSQL | System of record | Transactional writes, point lookups, the source every other store can be rebuilt from |
| StarRocks | Analytics & search | Sub-second filters, joins, and aggregations that would crawl on a row store |
| Qdrant | Vector search | Similarity search and RAG retrieval over server-side embeddings |
| Kafka | The fan-out | One ordered topic per entity key, durable and replayable — with retry, DLQ, and trace propagation |

## The query model

Five clients, one protobuf contract, two engines. The DSL builds offline — `build()` never needs a live server — and the server routes each RPC to the engine that's actually good at it:

| You want | Call | Engine |
|----------|------|--------|
| Rows matching filters, sorted and paged | `Search` | StarRocks |
| Multiple metrics per group, one round trip | `GroupBy` | StarRocks |
| Bucketed facets (terms, date histograms, ranges) | `Aggregate` | StarRocks |
| Chained CTE steps — joins against prior steps, windows, derived columns | `Pipeline` | StarRocks |
| "Find articles *about* this" from raw text | `SearchSimilar` | Qdrant |
| The best *passages* for a RAG prompt | `SearchChunks` | Qdrant |

```csharp
var query = Query.For<Article>()
    .Where(a => a.IsPublished, EqualTo, true)
    .Where(a => a.WordCount, GreaterThanOrEquals, 500)
    .OrderBy(a => a.PublishedAt, descending: true)
    .Page(0, size: 10);
```

The full guide — all five languages, joins, GroupBy, facets, and semantic search — is located at [`docs/one-query-five-languages.md`](docs/one-query-five-languages.md).

---

## What's in the repository

### Server (`Iverson.Server/`)

| Project | Description |
|---------|-------------|
| `Iverson.Api` | gRPC + HTTP entry point — schema registration, writes, reads, search, admin (reconcile/DLQ), `/metrics`. Same image also runs the Kafka projection consumers as the `worker` role (`WORKLOAD_ROLE=worker`) |
| `Iverson.Sql` | PostgreSQL access via Npgsql + Dapper |
| `Iverson.StarRocks` | StarRocks access over the MySQL wire protocol, with cold-start gating and a circuit breaker |
| `Iverson.Vector` | Qdrant vector storage and ANN search |
| `Iverson.Embeddings` | Local text embeddings via Ollama (`nomic-embed-text`, 768 dims) |
| `Iverson.Events` | Kafka producer/consumer with retry, DLQ, and W3C trace propagation |
| `Iverson.Launcher` | Local dev orchestrator — starts Docker, waits for readiness, spawns the API |
| `Iverson.LoadTest` | Load and benchmark tooling, targeting docker-compose or a live cluster |

### Clients (`Iverson.Clients/`)

Idiomatic client libraries generated from shared protos in `Iverson.Clients/Common/Proto/`: [.NET](Iverson.Clients/DotNet/), [Java](Iverson.Clients/Java/), [Python](Iverson.Clients/Python/), [Go](Iverson.Clients/Go/), [TypeScript](Iverson.Clients/TypeScript/).

### Deployment (`Iverson.Server/deploy/`)

The same umbrella Helm chart deploys everywhere — laptop to cloud:

| Target | What you get |
|--------|--------------|
| `docker-compose.yml` | The whole stack on one machine, one command — including Jaeger tracing and Prometheus metrics |
| `deploy/kind/` | A real Kubernetes cluster locally: Calico-enforced NetworkPolicy, operators (CloudNativePG, Strimzi, StarRocks), TLS+SCRAM Kafka, `api`/`worker` as separate Deployments |
| `deploy/terraform/` + `deploy/helm/` | Production clusters on **AWS, Azure, or GCP** — Terraform provisions EKS/AKS/GKE, node pools, and operators; Helm values overlays per cloud |

---

## Getting started

Run everything with the launcher:

```bash
cd Iverson.Server/Iverson.Launcher
dotnet run
```

It starts Docker, waits for every service (including the Ollama embedding model), and spawns the API. Ctrl+C tears it all down cleanly.

Or bring up the stack manually:

```bash
cd Iverson.Server
docker compose build iverson-api
docker compose up -d
```

Then watch a write travel through the whole system: open Jaeger at `http://localhost:16686`, select `Iverson.Api`, and follow one trace ID from the gRPC call through Kafka into all three stores. Every response also carries an `X-Trace-Id` header for client-side correlation. Prometheus is at `http://localhost:9090`, scraping `/metrics` from the API. (Consumer retry/DLQ counters and reconciliation/DLQ backlog gauges only populate once a `worker`-role instance is running — docker-compose runs `api` only; see `deploy/kind/` or `deploy/helm/` for the split deployment.)

---

## License

MIT — see [LICENSE](LICENSE).
