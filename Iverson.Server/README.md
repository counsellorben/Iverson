# Iverson

This repository is named after the original AI — Allen Iverson. Before large language models, before neural networks went mainstream, before anyone was asking ChatGPT anything, Allen Iverson was already doing the impossible with minimal resources, maximum efficiency, and zero regard for conventional wisdom. We practice the same philosophy here.

---

## Solution Overview

Iverson is a .NET 10 backend solution built around a CQRS + event-driven, polyglot-persistence architecture. The write path is thin: a gRPC API validates and publishes `EntityEvent` messages to Kafka. Three independent background consumers project each event into three stores by responsibility: **PostgreSQL** (system of record), **StarRocks** (engagement/search read store), and **Qdrant** (vector/RAG). Reads route to the appropriate store based on the query type.

All projects are instrumented with OpenTelemetry, sharing a single trace ID across every hop — including across Kafka via W3C `traceparent` headers.

---

## Projects

### Iverson.Api

**Type:** ASP.NET Core Web API

The entry point for all inbound HTTP and gRPC traffic. Registers and composes the other libraries via dependency injection, configures the OpenTelemetry pipeline (tracing + structured logging → Jaeger over OTLP), and exposes probe endpoints for validating each backend connection at runtime.

Every response includes an `X-Trace-Id` header containing the W3C trace ID, so callers can correlate a request to its full distributed trace in Jaeger.

**Key endpoints:**

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Liveness check — probes Postgres, StarRocks, Qdrant, and Kafka in parallel |
| `GET` | `/probe/sql` | Validates PostgreSQL connectivity |
| `GET` | `/probe/starrocks` | Validates StarRocks connectivity |
| `GET` | `/probe/vector` | Validates Qdrant connectivity |
| `POST` | `/probe/kafka` | Publishes a test message to Kafka |
| `POST` | `/admin/reconcile/{typeName}` | Re-projects all Postgres records for a type back through Kafka (re-syncs StarRocks + Qdrant from source of truth) |

**gRPC services:**

| Service | Responsibility |
|---------|---------------|
| `ObjectMappingGrpcService` | Schema registration; `RegisterSchema`, `Get`, `Post`, `Update`, `Delete` |
| `ObjectPersistenceGrpcService` | Thin write path; validates payload and publishes to Kafka |
| `ObjectRetrievalGrpcService` | Record reads from PostgreSQL |
| `ObjectSearchGrpcService` | SQL search/aggregate (StarRocks) and vector search (Qdrant) |

---

### Iverson.Sql

**Type:** Class Library
**Backend:** PostgreSQL (via Docker — `postgres:16`)
**Packages:** `Npgsql`, `Dapper`

Handles all relational database interactions. Exposes `IPostgresRepository`, a lightweight abstraction over parameterized SQL using Dapper for mapping. PostgreSQL is the **system of record** — the authoritative source of truth for all entities.

Registered via `services.AddPostgres(connectionString)`.

---

### Iverson.StarRocks

**Type:** Class Library
**Backend:** StarRocks (via Docker — `starrocks/allin1-ubuntu:latest`, MySQL wire on port 9030)
**Packages:** `MySqlConnector`, `Dapper`

Handles engagement analytics and full-text/filter search. Exposes `IStarRocksRepository` backed by `StarRocksRepository` using the MySQL wire protocol. Tables use the Primary Key model — INSERT of an existing key is a full-row replace, making re-delivery safe. Schema creation is idempotent via `ApplyTableAsync`.

Registered via `services.AddStarRocks(connectionString)`.

---

### Iverson.Vector

**Type:** Class Library
**Backend:** [Qdrant](https://qdrant.tech) (via Docker — `qdrant/qdrant:latest`)
**Packages:** `Qdrant.Client`

Handles vector storage and approximate nearest-neighbor search. Exposes `IVectorService` backed by `QdrantVectorService`, using the first-party Qdrant gRPC client. Collections are created on demand with cosine distance. Search results are returned as `VectorSearchResult` records carrying the point ID, similarity score, and any stored payload.

**Why Qdrant?** It ships as a single Docker image with no external dependencies, has a first-party .NET gRPC client, and is purpose-built for ANN search.

Registered via `services.AddQdrant(host, port)`.

---

### Iverson.Embeddings

**Type:** Class Library
**Backend:** [Ollama](https://ollama.com) (via Docker — `ollama/ollama:latest`)
**Model:** `nomic-embed-text` (768 dimensions)

Handles text embedding for vector search and RAG. Exposes `IEmbeddingService` that calls the local Ollama HTTP API. The model is pulled on first use by the `ollama-init` container; `Iverson.Launcher` waits for the model to appear in `/api/tags` before starting the API.

Registered via `services.AddEmbeddings(ollamaUrl)`.

---

### Iverson.Events

**Type:** Class Library
**Backend:** Apache Kafka (via Docker — `confluentinc/cp-kafka:7.6.0` + Zookeeper)
**Packages:** `Confluent.Kafka`

Handles event publishing and consumption. Exposes two interfaces:

- **`IEventProducer`** — publishes `EntityEvent` messages to a topic. Injects a `traceparent` W3C header into every Kafka message so the trace ID travels with the event.
- **`IEventConsumer`** — subscribes to a topic with a consumer group and invokes a handler per message.

Delivery contract (enforced by `MessageDispatcher`):
- Transient failures retry up to 3 times with exponential backoff (1 s / 2 s / 4 s).
- Deterministic failures (`PoisonMessageException` — malformed JSON, null deserialization) route directly to `iverson.entity.dlq` with no retry.
- Offsets are committed **only** after a successful projection or a successful DLQ write. A failed DLQ write halts the consumer rather than committing a lost event.
- `consumer.retries` and `consumer.dlq_routed` counters are exposed via `Meter "Iverson.Events"` for future metrics scraping.

Topics are created with 12 partitions (keyed by entity ID, preserving per-entity ordering while enabling horizontal consumer scaling).

Registered via `services.AddKafka(bootstrapServers)`.

---

### Iverson.Launcher

**Type:** Console Application

Orchestrates the full stack for local development. On startup it:

1. Runs `docker compose up -d` to bring up all infrastructure containers
2. TCP-polls each service port until it responds (PostgreSQL :5432, StarRocks :9030, Qdrant :6333, Kafka :9092, Ollama :11434, Jaeger :4317)
3. Waits for the `nomic-embed-text` model to appear in Ollama's model list
4. Spawns `Iverson.Api` as a child process
5. Listens for Ctrl+C, then kills the API process and runs `docker compose down`

This means a single `dotnet run` from the `Iverson.Launcher` directory is enough to start the entire system.

---

## Observability

All projects are instrumented with OpenTelemetry using BCL `System.Diagnostics.ActivitySource`. No OTEL packages are required in the class libraries — only the API project carries the OTEL SDK.

| Library | Activity Source | Span kinds |
|---------|----------------|------------|
| `Iverson.Sql` | `Iverson.Sql` | `Client` |
| `Iverson.StarRocks` | `Iverson.StarRocks` | `Client` |
| `Iverson.Vector` | `Iverson.Vector` | `Client` |
| `Iverson.Events` | `Iverson.Events` | `Producer` / `Consumer` |

Traces and logs are exported to **Jaeger** (`jaegertracing/all-in-one`) over OTLP gRPC on port `4317`. The Jaeger UI is available at `http://localhost:16686`.

Every span follows OpenTelemetry semantic conventions (`db.system`, `db.statement`, `messaging.system`, `messaging.destination`, etc.). Exceptions are recorded as `exception` events on the failing span. Consumer drops (schema not found) and DLQ routes are tagged on the span.

---

## Infrastructure

All backing services are defined in `docker-compose.yml`:

| Service | Image | Ports |
|---------|-------|-------|
| PostgreSQL | `postgres:16` | `5432` |
| StarRocks | `starrocks/allin1-ubuntu:latest` | `8030` (HTTP/admin), `9030` (MySQL query) |
| Qdrant | `qdrant/qdrant:latest` | `6333` (REST), `6334` (gRPC) |
| Ollama | `ollama/ollama:latest` | `11434` |
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
