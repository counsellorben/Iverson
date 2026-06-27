# Engagement Store: HTAP Storage Analysis

**Goal:** Identify the best open-source storage solution for the Iverson engagement store, which must serve a mixed workload of high-frequency Kafka writes (fan-out events), transactional point lookups, filtered set queries (EQUALS/IN/range), and ad-hoc analytical aggregations (TERMS, DATE_HISTOGRAM, CARDINALITY, AVG/SUM). This is an HTAP (Hybrid Transactional/Analytical Processing) problem.

---

## 1. TiDB

**Architecture:** Separates storage into TiKV (row-oriented, Raft-replicated, for OLTP) and TiFlash (columnar replica, for OLAP). Data written to TiKV is asynchronously replicated to TiFlash. The optimizer routes queries to the right engine automatically — point lookups hit TiKV, aggregations hit TiFlash.

**OLTP:** Strong. Full ACID, MySQL-compatible wire protocol, row-store point lookups at O(log n). Handles high-concurrency writes well. Supports distributed transactions.

**OLAP:** Very strong. TiFlash uses columnar MPP execution — vectorized, parallel, with predicate pushdown. TERMS aggregations, GROUP BY, and filtered aggregations perform comparably to dedicated analytics engines on large datasets.

**Kafka integration:** No native connector — consume via application code (which Iverson already does) or Kafka Connect JDBC sink. Standard path.

**CONTAINS/CARDINALITY:** CONTAINS requires `LIKE '%x%'` — no trigram index, full column scan on TiFlash (fast due to columnar scan, but not inverted index). CARDINALITY is exact COUNT DISTINCT — slower than ES HLL at very high cardinality but exact.

**Operational complexity:** High. Requires running TiDB server, TiKV nodes, TiFlash nodes, PD (placement driver), and TiSpark (optional). Minimum useful cluster is ~6 processes. Not suitable for a single-node dev environment without significant memory.

**Fit for Iverson:** Best architectural match for the HTAP requirement if you are willing to accept operational overhead. TiFlash replication means you never scan row-store data for aggregations. Best choice at scale (> 1M rows per type); significant ops cost at small scale.

---

## 2. ClickHouse

**Architecture:** Column-oriented, shared-nothing, vectorized execution. Data is stored in MergeTree family tables sorted by primary key. Columnar compression is aggressive (5–10x typical). Optimized almost entirely for analytical throughput.

**OLTP:** Weak in the traditional sense. ClickHouse has no row-level updates — mutations (`ALTER TABLE UPDATE`) are expensive background rewrites. The ReplacingMergeTree engine handles upserts eventually (deduplication on merge), but reads may see duplicates until merge completes. Lightweight deletes exist but are asynchronous. For an engagement store receiving fan-out events that may need to update records, this is a significant constraint.

**OLAP:** Exceptional. Fastest open-source engine for large-scale aggregations. TERMS, DATE_HISTOGRAM, and filtered aggregations at billions of rows are ClickHouse's core strength. Filter cache via PREWHERE, bloom filters on columns, skip indexes. CARDINALITY via `uniq()` (HLL-like, approximate) or `uniqExact()` (exact, slower).

**Kafka integration:** Native — ClickHouse has a built-in Kafka table engine that consumes topics directly. No Kafka Connect or application-side consumer needed.

**CONTAINS:** `LIKE '%x%'` triggers a column scan, but columnar compression and SIMD vectorization make it faster than Postgres sequential scan at scale. `ngrambf_v1` bloom filter indexes can prune data blocks. Not as structurally clean as ES inverted index, but competitive.

**Operational complexity:** Low-to-medium for single-node; medium for clustered. Single binary, good Docker support. Much simpler than TiDB.

**Fit for Iverson:** Excellent if you can tolerate eventually-consistent upserts. The Kafka consumer pattern (append-only with ReplacingMergeTree deduplication) works well for fan-out events. Best pure analytical performance; OLTP upsert consistency is the constraint to evaluate.

---

## 3. StarRocks

**Architecture:** MPP columnar engine forked from Apache Doris in 2020 (consistently outperforms Doris in TPC-H/TPC-DS benchmarks). Has a Primary Key table type (added 2022) that supports real-time upserts with row-level locking at the storage layer — the key differentiator from ClickHouse.

**OLTP:** Good for a columnar engine. Primary Key tables support upserts with sub-second latency. Internally maintains a delete bitmap over a sorted columnar store — inserts are merged incrementally rather than as expensive background rewrites. Point lookups are slower than TiKV or Postgres row store, but aggregations on recently upserted data are consistent.

**OLAP:** Excellent. Vectorized MPP execution, CBO (cost-based optimizer), materialized views that update incrementally on write. Comparable to ClickHouse on analytical benchmarks, sometimes faster on complex multi-join aggregations.

**Kafka integration:** Native Routine Load (streaming ingestion from Kafka topics, exactly-once semantics). No application-side consumer needed.

**CONTAINS:** Similar to ClickHouse — column scan with bloom filter acceleration. Not an inverted index.

**CARDINALITY:** `approx_count_distinct()` (HLL) and `count(distinct)` (exact). HLL is fast; exact is O(n log n) like Postgres.

**Operational complexity:** Similar to ClickHouse single-node. FE (frontend/metadata) + BE (backend/storage) separation; minimum viable is 1 FE + 1 BE. Simpler than TiDB.

**Fit for Iverson:** Best balance of OLTP and OLAP for a columnar engine. Primary Key upserts resolve ClickHouse's main weakness. Kafka Routine Load is a cleaner integration than application-side consumers. Strong candidate if the engagement store receives updates (not just inserts).

---

## 4. DuckDB

**Architecture:** In-process, embedded columnar analytical engine (like SQLite for analytics). Runs inside the application process. No separate server. Reads Parquet, Arrow, CSV, and can attach to Postgres via the `postgres` extension with direct query pushdown.

**OLTP:** Not designed for it. Single-writer only — concurrent write transactions are not supported in the traditional sense. No replication. Not suitable as a standalone persistent write-path store. Best used as a read-side analytical layer attached to a primary store.

**OLAP:** Exceptional for its size. Vectorized, parallel (uses all CPU cores), excellent aggregation performance. On a single node, often matches or beats ClickHouse for moderate dataset sizes (< 100M rows).

**Kafka integration:** None native. Would require materializing data to Postgres (existing write store) and querying via the Postgres extension, or periodically exporting to Parquet.

**CONTAINS:** Full-column scan; no skip indexes. Fast due to vectorization but not index-accelerated.

**Fit for Iverson:** Not viable as a standalone engagement store, but potentially powerful as a read-side query layer attached to the Postgres write store. Pattern: Postgres (write) → DuckDB (in-process query engine reading Postgres directly) for analytical queries. Eliminates the need for a separate engagement store altogether for moderate scale. Zero operational overhead. The limitation is that DuckDB queries Postgres live — heavy analytical queries block on Postgres I/O.

---

## 5. Apache Doris

**Architecture:** MPP columnar engine, predecessor to StarRocks. Has Duplicate Key, Aggregate Key, Unique Key (upsert), and Primary Key table models. Vectorized execution engine (added 2022). Large community, widely deployed in production HTAP scenarios.

**OLTP:** Similar to StarRocks Primary Key model — Unique Key and Primary Key tables support upserts. Slightly higher write latency than StarRocks on recent benchmarks, but functionally equivalent for Iverson's fan-out rates.

**OLAP:** Excellent. Slightly behind StarRocks on TPC-H but ahead of Postgres for large aggregations. Full SQL support including window functions.

**Kafka integration:** Routine Load (same pattern as StarRocks — they share the lineage).

**CONTAINS:** Same limitations as StarRocks/ClickHouse — columnar scan, no inverted index for substring matching.

**Fit for Iverson:** Functionally a peer to StarRocks; StarRocks is preferred based on benchmark trajectory, but Doris has a larger community and more production case studies in HTAP scenarios. Good alternative if StarRocks operational familiarity is a concern.

---

## Summary Matrix

| | TiDB | ClickHouse | StarRocks | DuckDB | Doris |
|---|---|---|---|---|---|
| **OLTP (upserts)** | Excellent | Weak (eventual) | Good | N/A | Good |
| **OLAP (aggregations)** | Very good | Exceptional | Excellent | Excellent (≤100M) | Very good |
| **CONTAINS performance** | Column scan | Bloom + column scan | Bloom + column scan | Column scan | Bloom + column scan |
| **CARDINALITY** | Exact (slow at scale) | HLL / exact | HLL / exact | Exact (parallel) | HLL / exact |
| **Kafka native** | No | Yes (engine) | Yes (Routine Load) | No | Yes (Routine Load) |
| **Operational complexity** | High | Low–medium | Low–medium | Zero | Low–medium |
| **Scale sweet spot** | > 1M rows | > 500K rows | > 500K rows | < 100M rows | > 500K rows |

---

## Recommendation for Iverson

**If the engagement store receives updates (fan-out rewrites existing records):** StarRocks is the strongest candidate. Primary Key upserts, Kafka Routine Load, and excellent aggregation performance directly address the workload. It replaces ES cleanly — TERMS and DATE_HISTOGRAM are native, CARDINALITY via HLL matches ES's accuracy profile.

**If the engagement store is append-only (fan-out only inserts):** ClickHouse becomes the top choice — simpler model, slightly better raw analytical throughput, same Kafka-native ingestion.

**If operational overhead must be zero and scale is moderate (< 50M rows):** DuckDB attached to the existing Postgres write store is the pragmatic answer. No new infrastructure — the analytical layer runs inside the application process.

**The CONTAINS gap** is real in all five options — none match ES's inverted index for substring search. If CONTAINS is a dominant query pattern at scale, ES remains the structurally correct choice. If CONTAINS is rare or on low-cardinality fields, any of the top three options cover the workload.
