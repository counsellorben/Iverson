# TPC-H and Iverson Benchmark Candidates

**Date:** 2026-06-30

---

## 1. What TPC-H Is

TPC-H is a decision-support benchmark published by the Transaction Processing Performance Council. It consists of 22 read-heavy SQL queries (plus two refresh functions) that simulate ad-hoc business intelligence workloads: aggregations, GROUP BY, ORDER BY, multi-table joins, subqueries, HAVING clauses. The schema has 8 tables (lineitem, orders, customer, supplier, part, partsupp, nation, region) with star/snowflake relationships designed to stress join planning, filter pushdown, and aggregate throughput.

### Scale Factors

The size of the generated dataset is controlled by a scale factor (SF), where SF 1 = approximately 1 GB of raw data.

| Scale Factor | Raw Size | lineitem rows |
|---|---|---|
| SF 1 | ~1 GB | ~6 million |
| SF 10 | ~10 GB | ~60 million |
| SF 100 | ~100 GB | ~600 million |
| SF 300 | ~300 GB | ~1.8 billion |
| SF 1000 | ~1 TB | ~6 billion |
| SF 3000 | ~3 TB | ~18 billion |
| SF 10000 | ~10 TB | ~60 billion |
| SF 30000 | ~30 TB | — |
| SF 100000 | ~100 TB | — |

SF 100 and SF 1000 are the most commonly cited in published comparisons. TPC-H SF 100 is widely used for vendor comparisons in the 100GB class; SF 1000 separates systems that scale gracefully from those that don't.

### Certified vs. Informal Results

The TPC maintains an official audit process. A **certified result** requires a third-party audit, priced hardware disclosure, and publication of all configuration details. Certified results carry legal weight and are published at tpc.org.

Most modern analytical databases publish **informal results** — they run the 22 queries on their own hardware without TPC audit, publish query times, and do not disclose pricing. These are still useful for directional comparison but cannot be cited as TPC-H certified results.

---

## 2. What TPC-H Tests (and Doesn't Test)

### What It Tests

- SQL aggregation throughput (GROUP BY, SUM, COUNT, AVG)
- Join efficiency across normalized tables
- Predicate pushdown and partition pruning
- Multi-query concurrency (optional "throughput test" with 8 streams)
- Cold and warm cache query plans

### What It Doesn't Test

- **Vector search** — no ANN queries, no embedding generation, no similarity ranking
- **Document/JSON workloads** — the schema is fully normalized relational; no semi-structured data
- **Write throughput** — only two refresh functions (insert/delete), not a OLTP write benchmark
- **End-to-end API latency** — no gRPC or HTTP overhead; results are database-internal query times
- **Schema flexibility** — the schema is fixed; no dynamic field registration
- **Semantic search or RAG** — no relevance ranking, no LLM integration

### Relevance to Iverson

Iverson routes Search and Aggregate calls to StarRocks, which is an MPP columnar store. TPC-H directly benchmarks this layer. However, Iverson as a whole is more than its analytics engine — it is a unified, multi-store API. A TPC-H result on StarRocks tells you how the analytics engine performs; it tells you nothing about:

- The gRPC overhead on the Iverson write or search path
- The Postgres key-retrieval path
- The Qdrant vector search path
- The Kafka fan-out latency and Qdrant catch-up lag
- The embedding inference cost (Ollama)

TPC-H is therefore a useful **lower-bound latency indicator for the Iverson analytics path** only. Any full Iverson comparison must also cover the write path and the vector path independently.

---

## 3. Candidates with Formal TPC-H Submissions

### SingleStore (formerly MemSQL)

**Relevance to Iverson:** Very high. SingleStore is the closest architectural peer to Iverson as a single product — it combines OLTP row storage with OLAP columnar storage in a single engine, and has added vector search support. Where Iverson distributes these responsibilities across Postgres, StarRocks, and Qdrant behind a gRPC API, SingleStore collapses them into one unified database.

**TPC-H stance:** SingleStore has published formal TPC-H submissions. Published informal results at SF 100 and SF 1000 show competitive performance against ClickHouse and Redshift. Their vector search support makes this the only formal TPC-H submitter that also competes on the vector path.

**Benchmark value:** Comparing Iverson to SingleStore answers the question of whether a purpose-built unified store outperforms a service that orchestrates specialized stores. This is the highest-value single comparison Iverson could run.

### Traditional OLAP Vendors (Oracle, Teradata, IBM)

**Relevance to Iverson:** Low. These are licensed enterprise warehouses not comparable to Iverson's open-source stack. They have formal TPC-H submissions at SF 10000+, but the cost and operational profile make them irrelevant as Iverson comparison targets.

---

## 4. Candidates with Published Informal TPC-H Results

### ClickHouse

**Relevance to Iverson:** High for the analytics path only. ClickHouse is the most widely cited informal TPC-H benchmark system in the OLAP community. It consistently places at or near the top of SF 100 and SF 1000 comparisons, often completing all 22 queries in under 30 seconds at SF 100 on commodity hardware.

**Positioning:** ClickHouse has no native vector search, no document store, and no unified API. It is a pure OLAP store. Comparing Iverson's StarRocks-backed aggregation path against ClickHouse tells you how much overhead Iverson's architecture adds versus using ClickHouse directly.

**Note:** StarRocks was benchmarked against ClickHouse extensively by its authors and by Cloudflare. StarRocks matches or beats ClickHouse on most TPC-H queries at SF 100 while adding MySQL-compatible DDL and better Flink integration.

### StarRocks (Iverson's current analytics engine)

**Relevance to Iverson:** Direct. StarRocks is already inside Iverson. Published StarRocks informal TPC-H results at SF 100 and SF 1000 exist in the StarRocks GitHub repo and third-party blogs.

**Benchmark use:** Running TPC-H against StarRocks in Iverson's docker-compose validates that Iverson's schema (JSON-column model vs. native StarRocks columns) does not introduce significant overhead. If Iverson's aggregation queries are materially slower than published StarRocks baselines, the overhead is attributable to the schema design or the gRPC path.

### Apache Doris

**Relevance to Iverson:** Moderate. Doris is architecturally similar to StarRocks (both forked from the Apache Impala/Palo lineage). Doris publishes TPC-H informal results. Comparing Iverson's StarRocks results to Doris is useful for validating the engine choice.

### DuckDB

**Relevance to Iverson:** Moderate for the analytics path, with important caveats. DuckDB is an embedded OLAP engine — it runs in-process, not as a service. Published DuckDB TPC-H results at SF 100 are fast (often best-in-class for single-node), achieved with no network overhead. DuckDB has no vector search, no replication, and no concurrent writer support in the open-source version.

**Benchmark value:** DuckDB's SF 100 results represent the best-case single-node analytical performance, making them a useful ceiling for single-node comparisons. Comparing Iverson (with StarRocks) against DuckDB quantifies how much StarRocks's service-oriented architecture costs versus an embedded analytical engine.

### TiDB

**Relevance to Iverson:** Moderate. TiDB is a distributed HTAP database (OLTP via TiKV, OLAP via TiFlash columnar engine). Like SingleStore, it combines transactional and analytical workloads. TiDB publishes informal TPC-H results on TiFlash. It has no native vector search capability in the open-source edition.

**Benchmark value:** TiDB is the "distributed Postgres-compatible HTAP" answer to Iverson's "multi-store service" answer. Comparing them answers whether distributing stores (Iverson's approach) or co-locating them in a single distributed database (TiDB's approach) performs better at scale.

### Databricks (Photon engine)

**Relevance to Iverson:** Low for head-to-head, high for aspiration. Databricks Photon has published exceptional TPC-H SF 1000 and SF 10000 results. It is a cloud-native, lake-house engine operating at TB scale, far above Iverson's current target deployment size.

**Benchmark value:** Databricks results are useful as a scale ceiling — they show what the current state of the art looks like at SF 1000+. Not a realistic Iverson comparison target unless Iverson is being positioned for multi-TB analytical workloads.

---

## 5. Multi-Modal Candidates Without TPC-H Results

These systems are relevant Iverson comparisons because of their feature overlap, but they either do not run TPC-H or do not have published results. They require a workload-specific benchmark rather than TPC-H.

### Vespa

**Architecture:** Search and serving engine. Tensor operations, BM25, ANN (HNSW), structured attribute filtering, custom ranking. Real-time feed API. No external Kafka or separate vector store — ranking and filtering are unified in the engine.

**TPC-H relevance:** None. Vespa uses YQL (Yahoo Query Language), not SQL. It is a serving engine, not an analytical database — TPC-H's multi-table join queries have no equivalent in Vespa's use case. Vespa does not publish TPC-H results and is not a valid TPC-H target.

**Iverson comparison axis:** Vespa is the closest peer to Iverson's *full-stack* capability — it unifies structured filtering, full-text relevance, and dense vector ANN in a single engine with a real-time write path. The comparison to Iverson is: does Vespa's all-in-one architecture (higher operational simplicity, single serving layer) outperform Iverson's specialized-store federation for the combined structured+vector search workload? This is a benchmark that must be designed from scratch using Iverson's own query patterns, not TPC-H.

### MongoDB Atlas

**Architecture:** Document store with optional Atlas Search (Lucene-backed), Atlas Vector Search (HNSW on Atlas), and Atlas Analytics (columnar engine in preview). All under one API.

**TPC-H relevance:** Marginal. MongoDB does not position itself as an OLAP system, and its multi-table TPC-H performance would be poor. Relevant comparison is document write/read throughput and hybrid search latency against Iverson's Postgres+Qdrant paths.

### Weaviate

**Architecture:** Purpose-built vector database with optional BM25 hybrid search and basic structured filtering. No analytics engine.

**TPC-H relevance:** None. Weaviate is a vector-first store with limited aggregation support. The relevant comparison to Iverson is on the SearchSimilar / SearchChunks path (Qdrant in Iverson vs. Weaviate), not on the analytics path.

### Typesense

**Architecture:** Open-source search engine targeting Algolia-compatible use cases. BM25 + vector hybrid. No analytics.

**TPC-H relevance:** None. Typesense is a search engine, not an analytical database. Comparison value is limited to the text-search and ANN search paths.

### Elasticsearch / OpenSearch

**Architecture:** Inverted index + BM25 ranking, dense vector ANN (HNSW), bucket and metric aggregations, optional SQL plugin. Distributed. No MPP columnar engine.

**TPC-H relevance:** Low. ES/OpenSearch can run TPC-H-like queries via its SQL plugin, but its aggregation performance is not competitive with columnar engines at SF 100+. Elasticsearch's CONTAINS and cardinality aggregation advantages were part of the analysis that led Iverson to adopt StarRocks — these advantages do not apply to TPC-H's join-heavy schema.

**Iverson comparison axis:** Elasticsearch was Iverson's previous engagement store. The historical comparison data in `docs/engagement-store-htap-analysis.md` already covers this ground.

### Marten

**Architecture:** .NET library — Postgres document store and event store. No separate analytics engine. No vector search (without pgvector extension).

**TPC-H relevance:** None for TPC-H. Marten targets Postgres and would run TPC-H as a standard Postgres workload. The relevant comparison is Iverson's write path and key-read path versus Marten's, which is covered in `docs/iverson-vs-marten.md`.

---

## 6. Recommended Benchmark Strategy

Iverson's workload has three distinct layers. A complete benchmark must cover all three separately:

### Layer 1: Analytics Path (StarRocks)

**Benchmark:** TPC-H at SF 10 (local Docker), SF 100 (cloud instance).

**Method:** Load the TPC-H schema into StarRocks directly (not through Iverson's gRPC API) and run the 22 queries. Compare results to:
- Published StarRocks baseline (validates schema design overhead is minimal)
- ClickHouse SF 100 published results (validates engine choice)
- SingleStore SF 100 (validates federated architecture vs. unified store)

**What this measures:** StarRocks engine efficiency independent of Iverson overhead.

### Layer 2: Write + Key-Read Path

**Benchmark:** Iverson's existing `BenchmarkArticle` load test.

**Method:** Run DirectSeeder at varying concurrency against Postgres. Measure write p50/p95 and GetMany p50/p95 at 100K, 400K, and 1M rows.

**Compare to:**
- Marten (library write path — eliminates the gRPC + Kafka floor)
- SingleStore OLTP write path
- MongoDB Atlas document write path

**What this measures:** Iverson's gRPC + Kafka write overhead and Postgres key-read performance.

### Layer 3: Vector + Semantic Search Path

**Benchmark:** Custom ANN workload using Iverson's nomic-embed-text vectors.

**Method:** Pre-generate embeddings for a known corpus. Run `SearchSimilarAsync` at varying concurrency and corpus size. Compare recall@10 and p50/p95 latency to:
- Weaviate (HNSW)
- Qdrant standalone (removes Iverson gRPC overhead, isolates the vector engine)
- Vespa (unified ANN + structured filter in one engine)

**What this measures:** Qdrant's ANN performance through Iverson versus alternatives, and whether Iverson's eventual-consistency fan-out (Kafka → Qdrant) creates observable recall gaps vs. synchronous indexing.

---

## 7. Priority Order for Comparison Work

Given Iverson's current state (StarRocks confirmed, Marten comparison document exists), the highest-value comparisons are:

1. **SingleStore** — only competitor that covers all three layers (OLAP + OLTP + vector) as a single product. Answers the fundamental architecture question.
2. **ClickHouse at SF 100** — validates that StarRocks was the right engine choice for the analytics path. Low implementation cost.
3. **Vespa** — highest-value multi-modal comparison for the search path. Requires building a Vespa schema and loader.
4. **DuckDB at SF 100** — establishes the single-node OLAP ceiling. Useful for documentation of trade-offs at the analytics layer.
5. **MongoDB Atlas** — broad multi-modal store, large market share, relevant for positioning docs.

TPC-D (the predecessor benchmark, retired 1999) has no modern relevance. Its scale factors (100MB, 1GB, 10GB, 100GB) are subsumed by TPC-H, and no modern system publishes TPC-D results.
