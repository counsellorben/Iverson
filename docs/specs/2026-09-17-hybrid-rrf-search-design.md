# Hybrid RRF Search (experimental) — Design

**Date:** 2026-09-17
**Status:** Approved design, assumptions verified; not yet planned
**Scope:** `SearchSimilar` and `SearchChunks` gain an optional, experimental `hybrid` field that
fuses the existing vector ranking with one or two StarRocks rankings by Reciprocal Rank Fusion.

## 1. Purpose and success criteria

An experiment with two StarRocks ranking legs:

1. **Lexical (BM25)** over the searched property's text: classic hybrid dense+sparse retrieval.
2. **Filter + sort (`rank_query`)**: rows matching caller-supplied clauses, in caller-supplied sort
   order, used as a soft boost.

**Success criteria (Ben, 2026-09-17):**

- **BM25 is scored.** The benchmark harness runs baseline vs lexical arms on SciFact and FreshStack,
  for both `.similar` and `.chunks`, using the existing report tooling's paired tests (nDCG@10, R@50).
- **`rank_query` is qualitative.** It must work end to end and be inspectable. The benchmark
  corpora carry no structured metadata or per-query filter/sort intent, so no relevance number
  exists for it.

`hybrid` absent means today's behaviour, bit-for-bit, on every path.

## 2. Why StarRocks has to compute BM25 by hand

Probed on `starrocks/allin1-ubuntu:4.1.1`, the version pinned in `docker-compose.yml` and the Helm
charts:

- **There is no relevance scoring.** `bm25()`, `score()` and `match_score()` all fail with
  "No matching function".
- **`MATCH` / `MATCH_ANY` / `MATCH_ALL` are boolean filters only.**
- **The GIN inverted index is gated behind the FE config `enable_experimental_gin`** (off by
  default). Even when enabled it gave no speedup: the 25K-row BM25 query ran in 1.23–1.34 s with a
  GIN prefilter vs 1.21–1.57 s without. Prefiltering also computes IDF over the matched subset
  only, which is wrong. **Not used.**
- **Hand-rolled BM25 in plain SQL works.** On 25K rows of ~1.5 KB, a 3-term query took about 0.4 s
  of SQL: ~1.2 s end to end minus ~0.85 s measured `docker exec` overhead. Rare-term documents
  ranked first.
- **Today's tables carry no text index.** `StarRocksSchemaManager.BuildCreateTableDdl`, and
  `CONTAINS` is `LIKE '%x%'`. **This design makes no DDL change.**

## 3. Request contract

`Iverson.Clients/Common/Proto/object_search.proto`:

```proto
// Experimental. Present = fuse the vector ranking with one or two StarRocks rankings by
// Reciprocal Rank Fusion. Absent = today's behaviour, bit-for-bit.
message HybridRank {
    bool        lexical    = 1;  // add a BM25 ranking over the searched property's text
    SearchQuery rank_query = 2;  // add a ranking: rows matching clauses, in sort order (soft boost)
}

message SearchSimilarRequest { ...existing 1–7...; HybridRank hybrid = 8; }
message SearchChunksRequest  { ...existing 1–7...; HybridRank hybrid = 8; }
```

The RRF constant is fixed at **k = 60**. It is not a request field (YAGNI; adding one later is a
single field).

**Client impact.** The .NET contracts project and the Java client both compile from the shared
proto at build time, so the new message simply appears in both. Go, Python and TypeScript have
checked-in generated code that is **not** regenerated; proto3 ignores the unknown field. Only
the .NET server and the LoadTest harness use the field.

### 3.1 Validation

Every failure below returns `InvalidArgument`.

| Condition | Reason |
|---|---|
| `hybrid` present with `lexical = false` and no `rank_query` | Nothing to fuse. |
| `rank_query` has no sort | Without `ORDER BY`, StarRocks row order is arbitrary (`StarRocksQueryBuilder.cs:136`). |
| A `rank_query` clause or sort property does not resolve to a column | `BuildWhere` and `BuildOrder` **silently skip** unresolvable properties. Unchecked, a typo becomes a dropped clause or an empty `ORDER BY`. |
| A `rank_query` clause or sort property is not in `AllowedFields` | Same gate `Search` applies (`BuildWhere` / `BuildOrder` throw `EngagementQueryTranslationException`). |
| `hybrid` present and the request's hard `filter` has an `IN` clause with an empty list | See §3.2. **Ruling: reject (Ben, 2026-09-17).** |

The key column is appended as the final `rank_query` sort tie-breaker, so the ranking is
deterministic.

### 3.2 Semantics

- **`rank_query` is a soft boost, not a filter.** A vector hit that fails its clauses still
  appears, ranked only by its other lists.
- **The request's hard `filter`, plus tenant and ownership scoping, applies to every list**,
  including both StarRocks queries. Otherwise a StarRocks-only hit could bypass the caller's own
  filter.
- **Empty `IN` is rejected when `hybrid` is set.**
  - Qdrant translates it to `Match(property, [])` (`IntelligenceFilterBuilder.cs:107`).
  - StarRocks `BuildIn` returns `null`, and `BuildWhere` drops the clause (`StarRocksQueryBuilder.cs:1050`).
  - So the StarRocks list would admit rows the vector side excludes, and no filtered Qdrant
    retrieve exists to cross-check. Only the SearchSimilar head path is exposed: the chunks
    filters accept `EQUALS` only.

## 4. The two StarRocks rankings

Both are new read methods on `IEngagementStoreSearchService`, implemented in `EngagementRepository`
beside `SearchAsync`:

- They run through `RunTenantScopedAsync` (tenant role, readiness gate, circuit breaker).
- They use `IsExpectedMissingResourceError`, so a missing role or table returns an empty list.
- They project **keys only**; payloads come from Qdrant (§5.3).
- `DisabledEngagementStoreSearchService` implements them by throwing
  `EngagementStoreDisabledException`, as it does for every other method.

**List depth** is `min(topK × OverFetchFactor, MaxPageSize)`, with `OverFetchFactor = 4` and
`MaxPageSize = 1000` by default. The cap exists because `CheckPageSize` rejects anything larger,
which would fail every hybrid request with `top_k > 250`. At rank 1000 an item contributes under
1/1060, so the truncation is immaterial. On SearchChunks the depth counts **parent documents**.

**Authorization and filter.** Both methods take the same
`IReadOnlyDictionary<string, AuthorizationConstraint>` that `Search` receives:

- The service builds it from the `AuthorizationDecision` it already holds, exactly as
  `EvaluateAuthorization` does (`ObjectSearchGrpcService.cs:1070`).
- Owner and tenant predicates are ANDed as separate parenthesised groups, following `BuildSearch`.
- The request's hard filter is ANDed as **one more parenthesised group**:
  `(rank clauses) AND (hard filter) AND owner AND tenant`. It keeps its own AND/OR logic and is
  never merged into the `rank_query` clause list, because `SearchQuery` cannot nest groups.

### 4.1 Lexical ranking: `BuildLexicalRank`

This is a new builder in `StarRocksQueryBuilder`. It reuses `BuildWhere` for the filter group and
adds owner and tenant predicates exactly as `BuildSearch` does.

**Query-side tokenizer (C#):**
1. Lowercase with the invariant culture.
2. Take maximal runs of `[a-z0-9_]`.
3. De-duplicate.

There is no stopword list; IDF handles common words. If the query yields no tokens, the lexical
list is empty, which is not an error. Empty query text is already rejected by the embed step.

**Why ASCII.** Probed on 4.1.1: StarRocks `\w` and `\b` are ASCII-only while `lower()` is
Unicode-aware. `café` tokenizes as `caf`, and `\bcafé\b` matches nothing. An ASCII token class is
the only rule under which query tokens and document tokens agree. **Non-English text is a known
limitation.**

**Document side (SQL)**, for the property's column `c`:

- Term frequency: `tf_i = regexp_count(lower(c), '\b' + token_i + '\b')`. Each pattern is a bound
  parameter; tokens are regex-safe by construction.
- Document length: `dl = regexp_count(lower(c), '\w+')`, the same tokenizer.
- Corpus statistics come from one aggregate over the **same WHERE the ranking uses**: tenant,
  ownership and hard filter. That aggregate gives `N`, `avgdl` and
  `df_i = SUM(IF(tf_i > 0, 1, 0))`.
- Score: `Σ_i ln(1 + (N − df_i + 0.5)/(df_i + 0.5)) · tf_i·(k1+1) / (tf_i + k1·(1 − b + b·dl/avgdl))`,
  with k1 = 1.2 and b = 0.75 as constants.
- Only rows where `Σ tf_i > 0` are returned, ordered by score descending then key, `LIMIT depth`.

Shape: `WITH s AS (SELECT key, dl, tf_1..tf_n FROM t WHERE …), g AS (SELECT count(*), avg(dl),
df_1..df_n FROM s) SELECT key, bm25 FROM s CROSS JOIN g WHERE … ORDER BY bm25 DESC, key LIMIT d`.
This was probed on 4.1.1 with literals.

### 4.2 Sort ranking: `rank_query`

This runs through the existing `BuildSearch` path, extended with:
- a key-only projection
- the hard-filter group
- the key tie-breaker.

It uses `page = 0` and `pageSize = depth`. The existing clause-count and page-size limits
continue to apply.

### 4.3 Joining to vector ids

Each StarRocks key is mapped to its Qdrant id with `IntelligenceStoreConsumer.KeyToUlong`.
- The StarRocks key column is the entity's Guid string (`VARCHAR(36)`, `SchemaBuilder.cs:390`),
  written from the same event key the intelligence consumer uses.
- `KeyToUlong` parses Guids format-insensitively, so both sides derive the same id.

## 5. Fusion

### 5.1 Placement

RRF runs **after** `ResultReranker` and **before** MMR:

| Path | Vector list fed to RRF | StarRocks ranks join on |
|---|---|---|
| SearchSimilar, head path | `ResultReranker` output (entity ids) | entity id |
| SearchSimilar, chunks-routed (`SimilarViaChunksTypes`) | the max-passage-collapsed document list | entity id |
| SearchChunks | the fused chunk list | the chunk's **parent** id (every chunk of a document inherits that document's StarRocks rank) |

`rrf(d) = Σ_lists 1/(60 + rank_l(d))`, with 1-based ranks. A list not containing `d` contributes
nothing. Ties break by vector-list rank, then lexical rank, then `rank_query` rank (an absent
rank sorts after any present one), then key.

The RRF function is pure and I/O-free, placed beside `ResultReranker` in `Iverson.Vector`.

### 5.2 Score rescale before MMR (all three paths)

Before MMR, RRF scores are linearly rescaled onto the vector pool's own fused-score range
`[min, max]`. If every score is equal, all map to `max`.

- **Why.** `ResultDiversifier` scores `λ·score − (1−λ)·maxSim` (`ResultDiversifier.cs:70`). Raw RRF
  scores top out near 0.0164, with a rank-1 → rank-50 spread of about 0.007. At
  `LambdaChunks = 0.70` the penalty term, 0.3 × cosine (0.5–0.9), would be 20–40× the relevance
  term, and MMR would select almost purely for dissimilarity.
- **Effect.** Rescaling keeps the ordering (it is monotone) and gives MMR's relevance term the same
  spread it has in the baseline arm, so the experiment measures fusion, not an MMR side effect.
  At `LambdaSimilar = 1.00` it has no effect.
- **What is streamed.** The `score` field carries the rescaled value, consistent with the
  proto's existing "use for ordering only" caveat.

### 5.3 StarRocks-only hits

- **SearchSimilar (both paths).** Payloads are hydrated with `vector.RetrievePayloadAsync` on the
  object collection. It canonicalises payloads exactly as `SearchNamedAsync` does
  (`IVectorRoles.cs:25-30`), so `WriteSimilarResponsesAsync` emits the same shape.
  - Their `<property>_centroid` vectors are fetched through `RetrieveVectorsOrDegradeAsync` along
    with everyone else's. Otherwise an operator-set `LambdaSimilar < 1` would give them no MMR
    penalty and systematically favour them.
  - A hit missing at hydration is skipped and logged, as the chunks-routed path already does.
- **SearchChunks: re-rank only (Ben, 2026-09-17).** A document found only by StarRocks has no
  chunk in the pool and is dropped. On SearchChunks the StarRocks rankings reorder the passages
  the vector search retrieved; **they cannot add recall.** BM25's recall contribution is measured
  on SearchSimilar.

## 6. Failure behaviour

| Situation | Behaviour |
|---|---|
| StarRocks unreachable, not ready, or circuit open (`EngagementNotReadyException` or a non-expected `MySqlException`) | **Fail the RPC with `Unavailable`.** Deliberately not degraded to vector-only: an arm that silently lost its lexical list would score as "BM25 did nothing". A raw `MySqlException` escapes `RunTenantScopedAsync` before the circuit opens and would otherwise surface as `Unknown`. |
| Tenant role or table missing | That list is empty; fusion proceeds. |
| StarRocks disabled in this deployment (`EngagementStoreDisabledException`) | `FailedPrecondition`, as `Search`. |
| `EngagementQueryTranslationException` from either builder | `InvalidArgument`, as `Search`. |
| Query text yields no tokens | Lexical list empty; not an error. |
| `hybrid` absent | Today's code path, untouched. |

## 7. Harness

- **New flag.** One flag on `BenchmarkQueryScenario` sets `hybrid.lexical = true` on both RPCs. It
  is recorded in `<label>.meta.json` alongside the existing sidecar keys
  (`BenchmarkQueryScenario.cs:206-222`).
- **No `rank_query` flag.** That leg is qualitative.
- **Run precondition.** Before any hybrid run, the tenant's StarRocks row count for
  `BenchmarkDocument` must equal the Qdrant object-collection point count. The existing corpora
  were ingested through the write path, which feeds `EngagementStoreConsumer`, but whether the
  preserved volumes hold those rows has not been checked (the stack was down).

## 8. Testing

- **Unit:**
  - the RRF function (ranks, lists missing a document, ties)
  - the rescale (including all scores equal)
  - the tokenizer (ASCII runs, underscores, non-ASCII, de-duplication, no tokens)
  - `BuildLexicalRank` SQL shape and parameters, following `StarRocksQueryBuilderTests`
  - the extended `BuildSearch` (key-only projection, hard-filter group, tie-breaker)
- **Container integration against `starrocks/allin1-ubuntu:4.1.1`**, following
  `StarRocksRepositorySearchTests`:
  - BM25 ranks rare-term documents first
  - the bound `\b…\b` patterns survive MySqlConnector parameter substitution (probed with SQL
    literals only)
  - hard filter, ownership and tenant are respected
  - a missing table returns empty
- **gRPC service** (`ObjectSearchGrpcServiceTests`, NSubstitute):
  - every §3.1 rejection
  - a boosted document outranks its vector-only position
  - a StarRocks-only hit is hydrated on SearchSimilar and dropped on SearchChunks
  - StarRocks failure → `Unavailable`
  - `hybrid` absent → the StarRocks methods are never called

## 9. Known issues / accepted

- **Cross-store filter semantics are not proven identical beyond empty `IN`.** SQL `<>` excludes
  NULLs where Qdrant `must_not` includes points lacking the field, which makes StarRocks stricter
  (the safe direction). Timestamp comparison formats are unexamined.
- **The tokenizer is ASCII-only** (§4.1).
- **Lexical scoring is a full scan** of the tenant's table per query (about 0.4 s at 25K docs,
  roughly linear). Acceptable for an experiment; not a production retrieval path.
- **RRF k, BM25 k1 and BM25 b are uncalibrated constants.**

## 10. Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| A1 | Tenant-scoped read helper reusable | `EngagementRepository.RunTenantScopedAsync` + `IsExpectedMissingResourceError`, used by all 4 read paths |
| A2 | `BuildWhere` reusable | `StarRocksQueryBuilder.cs:555` takes clauses, logic, params, authz |
| A3 | Constraint carries tenant + ownership | `AuthorizationConstraint(AllowedFields, OwnerColumn, OwnerValue, TenantColumn, TenantValue)`; `BuildSearch` appends both |
| A4 | Vector RPCs can build the same constraint | `EvaluateAuthorization` builds it from `AuthorizationDecision` (`ObjectSearchGrpcService.cs:1070`) |
| A5 | SearchSimilar filter columns exist in StarRocks | Every non-key property is a scalar column (`SchemaBuilder.cs:60-64`), FKs included. **Found:** empty-`IN` divergence (§3.2) |
| A6 | Chunk filter columns exist in StarRocks | Metadata properties are scalars; key resolves via `ResolveColumn` |
| A7 | Predicate groups are ANDed separately | `BuildSearch` wraps `(where) AND owner`, `(…) AND tenant` |
| A8 | Unauthorized sort rejected | `BuildOrder` throws; **found:** unknown fields silently skipped → validated (§3.1) |
| A9 | Key → id agreement | Key is a Guid `VARCHAR(36)`; `KeyToUlong` uses `Guid.TryParse` |
| A10 | Hydration payload shape | `IVectorRoles.cs:25-30` |
| A11 | Regex patterns via bound parameters | Probed with literals; pinned by integration test (§8) |
| A12 | Token-count document length | `regexp_count(lower(c), '\w+')` probed |
| A13 | Tokenizer agreement | Probed: ASCII `\w`/`\b`, Unicode `lower()` → ASCII tokenizer (§4.1) |
| A14 | Body text reaches StarRocks | `EngagementStoreConsumer` upserts the full payload; volume contents unchecked → precondition (§7) |
| A15 | Depth bounded | **Found:** `MaxPageSize = 1000` → cap (§4) |
| A16 | Harness flag + sidecar pattern | `BenchmarkQueryScenario.cs:206-222` |
| A17 | Field 8 and name free | Both requests use 1–7; `HybridRank` appears nowhere |
| A18 | Client builds tolerate the field | .NET contracts + Java compile from the shared proto; Go/Python/TS are checked in; no CI regenerate/diff step |
| A19 | No shape dependents | No descriptor/field-number reflection in any client or test |
| A20 | Interface implementers | `EngagementRepository`, `DisabledEngagementStoreSearchService`; all tests use NSubstitute |
| A21 | Outage mapping | `EngagementNotReadyException` only when the circuit is open; raw `MySqlException` otherwise → explicit mapping (§6) |
| A22 | Filter/tenant/ownership on every list × path | §3.2 + §4; the only divergence found (empty `IN`) is rejected; the chunks paths are `EQUALS`-only |
| A23 | Absent diversity vectors | Fixed by fetching StarRocks-only centroids (§5.3) |
| A24 | CTE + aggregate `CROSS JOIN` | Probed on 4.1.1 |
