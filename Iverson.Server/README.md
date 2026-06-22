# Iverson

This repository is named after the original AI — Allen Iverson. Before large language models, before neural networks went mainstream, before anyone was asking ChatGPT anything, Allen Iverson was already doing the impossible with minimal resources, maximum efficiency, and zero regard for conventional wisdom. We practice the same philosophy here.

---

## Solution Overview

Iverson is a .NET 10 backend solution built around a modular, library-first architecture. A central Web API (`Iverson.Api`) coordinates multiple specialized class libraries, each owning its connection to a distinct data store or messaging system. All projects are instrumented with OpenTelemetry, sharing a single trace ID across every hop.

---

## Projects

### Iverson.Api

**Type:** ASP.NET Core Web API

The entry point for all inbound HTTP traffic. Registers and composes the other four libraries via dependency injection, configures the OpenTelemetry pipeline (tracing + structured logging → Jaeger over OTLP), and exposes probe endpoints for validating each backend connection at runtime.

Every response includes an `X-Trace-Id` header containing the W3C trace ID, so callers can correlate a request to its full distributed trace in Jaeger.

**Key endpoints:**

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Liveness check |
| `GET` | `/probe/sql` | Validates PostgreSQL connectivity |
| `GET` | `/probe/elasticsearch` | Validates Elasticsearch connectivity |
| `GET` | `/probe/vector` | Validates Qdrant connectivity |
| `POST` | `/probe/kafka` | Publishes a test message to Kafka |

---

### Iverson.Sql

**Type:** Class Library  
**Backend:** PostgreSQL (via Docker — `postgres:16`)  
**Packages:** `Npgsql`, `Dapper`

Handles all relational database interactions. Exposes `IPostgresRepository`, a lightweight abstraction over parameterized SQL using Dapper for mapping. Each method opens and disposes its own connection, keeping the surface area simple and avoiding connection-lifetime bugs.

Registered via `services.AddPostgres(connectionString)`.

---

### Iverson.Elasticsearch

**Type:** Class Library  
**Backend:** Elasticsearch (via Docker — `elasticsearch:8.13.0`)  
**Packages:** `Elastic.Clients.Elasticsearch`

Handles document indexing, retrieval, full-text search, and deletion against Elasticsearch. Exposes `IElasticsearchService` using the official v9 .NET client. Index creation is idempotent — the service checks for existence before issuing a create request.

Registered via `services.AddElasticsearch(url)`.

---

### Iverson.Vector

**Type:** Class Library  
**Backend:** [Qdrant](https://qdrant.tech) (via Docker — `qdrant/qdrant:latest`)  
**Packages:** `Qdrant.Client`

Handles vector storage and approximate nearest-neighbor search. Exposes `IVectorService` backed by `QdrantVectorService`, using the first-party Qdrant gRPC client. Collections are created on demand with cosine distance. Search results are returned as `VectorSearchResult` records carrying the point ID, similarity score, and any stored payload.

**Why Qdrant?** It ships as a single Docker image with no external dependencies, has a first-party .NET gRPC client, and is purpose-built for ANN search. Alternative Docker-native vector databases include:

| Database | Image | Notes |
|----------|-------|-------|
| **Qdrant** *(used)* | `qdrant/qdrant` | First-party .NET client, gRPC + REST |
| Weaviate | `semitechnologies/weaviate` | GraphQL API, strong hybrid search |
| Milvus | `milvusdb/milvus` | Enterprise-grade, more complex setup |
| Chroma | `chromadb/chroma` | Simple REST API, Python-native |

Registered via `services.AddQdrant(host, port)`.

---

### Iverson.Events

**Type:** Class Library  
**Backend:** Apache Kafka (via Docker — `confluentinc/cp-kafka:7.6.0` + Zookeeper)  
**Packages:** `Confluent.Kafka`

Handles event publishing and consumption. Exposes two interfaces:

- **`IEventProducer`** — publishes typed or raw string messages to a topic. Serializes objects to JSON automatically. Injects a `traceparent` W3C header into every Kafka message so the trace ID travels with the event.
- **`IEventConsumer`** — subscribes to a topic with a consumer group, invokes a handler per message, and commits offsets manually after successful processing. Restores the incoming trace context from the `traceparent` header, linking consumer spans back to the originating producer span.

Registered via `services.AddKafka(bootstrapServers)`.

---

### Iverson.Launcher

**Type:** Console Application

Orchestrates the full stack for local development. On startup it:

1. Runs `docker compose up -d` to bring up all infrastructure containers
2. TCP-polls each service port until it responds (PostgreSQL :5432, Elasticsearch :9200, Qdrant :6333, Kafka :9092, Jaeger :4317)
3. Spawns `Iverson.Api` as a child process
4. Listens for Ctrl+C, then kills the API process and runs `docker compose down`

This means a single `dotnet run` from the `Iverson.Launcher` directory is enough to start the entire system.

---

## Observability

All projects are instrumented with OpenTelemetry using BCL `System.Diagnostics.ActivitySource`. No OTEL packages are required in the class libraries — only the API project carries the OTEL SDK.

| Library | Activity Source | Span kinds |
|---------|----------------|------------|
| `Iverson.Sql` | `Iverson.Sql` | `Client` |
| `Iverson.Elasticsearch` | `Iverson.Elasticsearch` | `Client` |
| `Iverson.Vector` | `Iverson.Vector` | `Client` |
| `Iverson.Events` | `Iverson.Events` | `Producer` / `Consumer` |

Traces and logs are exported to **Jaeger** (`jaegertracing/all-in-one`) over OTLP gRPC on port `4317`. The Jaeger UI is available at `http://localhost:16686`.

Every span follows OpenTelemetry semantic conventions (`db.system`, `db.statement`, `messaging.system`, `messaging.destination`, etc.). Exceptions are recorded as `exception` events on the failing span.

---

## Infrastructure

All backing services are defined in `docker-compose.yml`:

| Service | Image | Ports |
|---------|-------|-------|
| PostgreSQL | `postgres:16` | `5432` |
| Elasticsearch | `elasticsearch:8.13.0` | `9200`, `9300` |
| Qdrant | `qdrant/qdrant:latest` | `6333` (REST), `6334` (gRPC) |
| Zookeeper | `confluentinc/cp-zookeeper:7.6.0` | — |
| Kafka | `confluentinc/cp-kafka:7.6.0` | `9092` |
| Jaeger | `jaegertracing/all-in-one:latest` | `4317` (OTLP gRPC), `4318` (OTLP HTTP), `16686` (UI) |

---

## Getting Started

**Run everything with the launcher:**

```bash
cd Iverson.Launcher
dotnet run
```

**Or bring up infrastructure manually and run the API:**

```bash
docker compose up -d
cd Iverson.Api
dotnet run
```

**View traces:**  
Open `http://localhost:16686` and select `Iverson.Api` from the service dropdown.
