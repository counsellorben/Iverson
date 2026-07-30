# Tensor Re-Ranking / Fusion Scoring — Design

Date: 2026-07-29
Status: Approved design, not yet planned or implemented

**Part 3 of the metadata / tensor-search initiative.**

## Context

The initiative adds a metadata layer and server-side tensor scoring to Iverson to improve
semantic-search precision and cut downstream LLM token spend. Its parts are (1) metadata
foundation, (2) Ollama ingest enrichment, (3) **this part — tensor re-ranking/fusion**,
(4) derived vector signals, and (5) an agent-facing schema/query surface.

Parts 1, 2 and 4a are merged. Part 4a deliberately preceded this one so the re-rank would
have a document-level signal to score against: every chunked field now carries a
`<field>_centroid` named vector on its object point, and nothing reads it yet. This part is
that reader.

**Base branch:** `main`, at the 4a merge `f989dc9`.

## Goal

Re-rank `SearchChunks` and `SearchSimilar` results server-side by fusing the query-to-vector
cosine Qdrant already returns with two additional signals — document-level centroid
similarity and a recency decay — so that the top `top_k` results a RAG consumer reads are
better ones.

## Design

### 1. Placement

One new component in `Iverson.Vector`, alongside the existing vector roles: an
`IResultReranker` with a single implementation. Its contract:

> given the query vector, the candidate list, each candidate's centroid vector (where one
> exists) and each candidate's decay value (where one exists), return the candidates
> re-scored and sorted.

It performs **no I/O**. Retrieval and the centroid fetch stay in the caller; the re-ranker is
pure computation over vectors handed to it. That makes it unit-testable against known inputs
with no container, and it is why both RPCs can share one scorer despite having different
fetch paths.

`ObjectSearchGrpcService.SearchSimilar` (the `SearchNamedAsync` call at `:202`) and
`SearchChunks` (`:303`) each gain the same four steps: over-fetch, assemble re-ranker inputs,
call the re-ranker, trim to `top_k`.

The scoring math uses `System.Numerics.Tensors` (`TensorPrimitives`), a new package reference
on `Iverson.Vector`.

### 2. The fused score

Three signals with fixed server-side weights:

| Signal | Weight | Meaning |
|---|---|---|
| `base` | 0.60 | Query-to-vector cosine Qdrant returned. Query vs. the chunk vector (`SearchChunks`) or the object's `<field>_vector` (`SearchSimilar`). |
| `centroid` | 0.30 | Query vs. the parent document's `<field>_centroid` — topicality of the whole document, which is what part 4a produced. |
| `decay` | 0.10 | `0.5 ^ (age / halfLife)` on the convention-selected timestamp field, `halfLife` = 180 days. |

The fused score is the weighted mean **over the signals actually present for that candidate**:

```
fused = Σ(wᵢ · sᵢ) / Σ(wᵢ)      for each present signal i
```

- All three present: `0.60·base + 0.30·centroid + 0.10·decay`.
- No centroid stored: `0.857·base + 0.143·decay`.
- No decay field: `0.667·base + 0.333·centroid`.
- Neither: `base` — **bit-for-bit today's behavior**.

Renormalization rather than a zero (or a neutral `1.0`) for absent signals is deliberate. A
document with no centroid scored `centroid = 0` would lose 30% of its score and rank below
essentially every document that has one, regardless of relevance — and a document is in that
state for ordinary reasons: its chunk field was blank on the event that wrote it, or §7's
dimension check rejected the stored centroid. Renormalizing makes an absent signal
neither help nor hurt, and it gives the feature the property of being inert for types that
have nothing to say: a type with no chunk fields and no timestamp metadata scores exactly as
it does today.

Because the weights are a mean, a fused score stays in the same range as today's raw cosine.

Two consequences of fixed constants, accepted deliberately:

- **Re-ranking is unconditional.** There is no per-request opt-out, so every existing
  `SearchChunks` / `SearchSimilar` caller gets reordered results once this ships. With 0.60 on
  the original signal, order changes will be common but not wholesale.
- **The returned `score` changes meaning** — it becomes the fused value, not raw cosine. No
  proto change is required, but a client comparing scores across versions will see a shift.

### 3. Over-fetch

Both RPCs request `4 × top_k` candidates from Qdrant, re-score all of them, sort, and return
the first `top_k`. Without over-fetching, fusion could only shuffle results the caller was
already getting; a strong document ranked `k+1` by raw cosine would stay invisible. Four is
enough for a re-ranker weighting the original signal at 0.60 to promote a genuinely better
result, and small enough to keep Qdrant's work in the same order of magnitude.

No cap is placed on the resulting candidate count. A caller asking for `top_k = 1000` causes a
4000-candidate fetch — that is the caller's own request scaled by a constant, and inventing a
ceiling would silently violate the `top_k` contract in the case a caller was most deliberate
about.

### 4. Centroid retrieval — one path serves both RPCs

In both cases the centroids live on **object points in the object collection**, so a single
helper covers both:

- `SearchSimilar` — the candidates *are* object points; fetch centroids for their own ids.
- `SearchChunks` — the candidates are chunk points; fetch centroids for their distinct
  **parent** ids.

Collect the distinct object point ids, issue **one** Qdrant retrieve against the object
collection requesting vectors, build an `id → centroid` map, and hand it to the re-ranker. One
extra round trip per search, batched — not per result. `SearchChunks` typically has many
chunks sharing few parents, so the map is usually much smaller than the candidate list.

A chunk's parent id is derived from its payload: the consumer writes `parent_id = ev.Key`
(`IntelligenceStoreConsumer.cs:216`), and the object point id is `KeyToUlong(key)` (`:100`).

The object collection name is resolved with the existing tenant-scoped call
(`ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks: false)`), and
the retrieve runs inside its own `RequestHeaders.Use("api-key",
tenantScope.MintScopedApiKey(objectCollection, readOnly: true))` scope — the same pattern the
two searches already use for their own collections.

### 5. Which centroid

The centroid vector name is `<chunk field in snake_case>_centroid`, matching what
`SchemaBuilder` declares (`SchemaBuilder.cs:219`).

- **`SearchChunks`** — the request's `property` is a chunk field by definition, so the centroid
  always exists.
- **`SearchSimilar`** — the request's `property` is an `[IversonEmbedding]` field, and
  centroids are named after **chunk** fields. The centroid signal is therefore used **only
  when the searched property carries both `[IversonEmbedding]` and `[IversonChunk]`**, which is
  the only case where `<property>_centroid` exists. Otherwise the signal is absent and §2's
  renormalization handles it.

Scoring one field's match against a *different* field's topicality was considered and rejected:
it is a different claim than the one the score purports to make.

### 6. The decay field convention

Among the type's declared `[IversonMetadata]` fields, consider those of timestamp type
(`MetadataColumns` joined to `ScalarColumns[].SqlType`, where `ClrDatetime` maps to
`TIMESTAMPTZ` / `DATETIME`):

| Timestamp metadata fields | Behavior |
|---|---|
| Exactly one | It is the decay field. |
| None | Decay signal absent; §2 renormalizes. |
| Two or more | Decay signal absent, and the server logs once per type. |

The two-or-more case refuses to guess deliberately. Choosing `CreatedAt` over `UpdatedAt` by
alphabetical accident would produce rankings nobody could explain from the outside, and the
failure would be silent. Absent-and-logged is diagnosable.

The decay **value** needs no extra fetch. Part 1 denormalizes declared metadata onto chunk
points (`IntelligenceStoreConsumer.cs:223-235`) and object points carry all their scalar
columns (`BuildObjectPointPayload`, `:340-348`), so the timestamp arrives in the payload the
search already returns. Today that value is **whatever the client's JSON serializer emitted**:
`ExtractTypedValue` (`:612-628`) has no timestamp case, so a `TIMESTAMPTZ` column falls through
to `v.GetString()` and is stored verbatim. §8 adds that case so the value is canonicalized on
write. Existing data is wiped before part 3 ships, so every point carries the canonical form —
there is no mixed-format corpus to read across.

### 7. Degradation

The re-rank is on the read path; its failures must not take search down.

| Failure | Behavior |
|---|---|
| Centroid retrieve throws | Log, fall back to raw-cosine ordering trimmed to `top_k`. A degraded ranking beats a failed search. |
| Query/centroid dimension mismatch | Treat that candidate's centroid signal as absent. Reachable in practice: an embedding-model change leaves old centroids at the old dimension until republish. |
| Unparseable or null decay value | Treat the decay signal as absent. |

No retries and no circuit breaker: the Qdrant client already carries this codebase's resilience
policy, and a second layer would only add latency to a path that already has a working
fallback.

### 8. Supporting changes

- **New role method.** `IVectorQueryService` exposes only `SearchAsync` and `SearchNamedAsync`
  (`IVectorRoles.cs:5-17`); the centroid fetch needs a retrieve-by-ids-with-named-vectors
  method, plus its `IntelligenceVectorService` implementation.
- **`KeyToUlong` visibility.** It is `private static` on `IntelligenceStoreConsumer` (`:564`).
  The search service needs the same key→point-id function; it moves to a shared `internal`
  helper in the same assembly. The function itself does not change — both sides must derive
  identical ids.
- **Timestamp normalization on write.** `ExtractTypedValue`
  (`IntelligenceStoreConsumer.cs:612-628`) gains a `TIMESTAMPTZ`/`DATETIME` case that parses to
  `DateTimeOffset`, so `ToQdrantValue`'s existing `"o"` branch
  (`IntelligenceVectorService.cs:184-185`) produces round-trip strings. Without it the decay
  signal's input format is client-determined and recency silently never applies for clients
  that don't emit ISO-8601. The helper is shared with the object-point payload, so this changes
  the stored format for every declared timestamp column, not only the decay field. **The read
  side must apply the same rule, in the shared translator rather than per RPC:**
  `IntelligenceFilterBuilder` canonicalizes the operand for `EQUALS`, `NOT_EQUALS` and `IN` —
  the three operators that reach a payload string comparison (`IntelligenceFilterBuilder.cs:73-79`,
  `:105`) — whenever the target column's `SqlType` is `TIMESTAMPTZ`/`DATETIME`. Placing it there
  covers both `SearchChunks` and `SearchSimilar` by construction; `SearchSimilar` admits any
  scalar column as a filter target (`ObjectSearchGrpcService.cs:594-595`) and passes the clause
  value through untouched (`:152`), so a per-call-site fix would leave it broken. The builder
  does not receive column types today; both call sites already hold `schema`, so passing the
  timestamp columns' names is the mechanical part. Without this, a timestamp filter silently
  matches nothing — or, under `NOT_EQUALS`, returns everything the caller asked to exclude —
  and the re-ranker scores the wrong candidate set.

### 9. Testing

- **Re-ranker unit tests** — known-input tests pinning the formula: all three signals; centroid
  promoting a candidate past one ranked above it; decay as tiebreaker between equal-cosine
  candidates; each absent-signal renormalization case; dimension mismatch; the base-only case
  proving today's ordering is preserved exactly.
- **Decay convention tests** — zero / one / many timestamp metadata fields.
- **Service-level tests** — the `4 × top_k` multiplier actually requested from Qdrant; results
  trimmed to `top_k`; the centroid retrieve batched to distinct parent ids; the fallback path
  when the retrieve throws; `SearchSimilar` with and without a dual-annotated property.
- **Integration** — the existing real-Qdrant fixture (`QdrantIntegrationTests`) covers the new
  retrieve-with-vectors call. A mock cannot confirm that Qdrant returns named vectors in the
  shape assumed; part 4a's final review caught exactly this class of gap.

## Verified assumptions

Checked against the codebase at `f989dc9` before this spec was written.

| # | Assumption | Result |
|---|---|---|
| A1 | Search results carry the point id | ✅ `VectorSearchResult(ulong Id, double Score, IReadOnlyDictionary<string,string> Payload)` — `IVectorRoles.cs:45` |
| A2 | `SearchNamedAsync` returns the payload | ✅ `payloadSelector: true` — `IntelligenceVectorService.cs:129`, stringified at `:138` |
| A3 | A retrieve-by-id-with-vectors call exists | ❌ **No such method.** `IVectorQueryService` has only `SearchAsync`/`SearchNamedAsync` (`IVectorRoles.cs:5-17`). Added as §8 work. |
| A4 | Chunk payload carries the parent key, and the object point id is derivable | ✅ with a caveat: `parent_id = ev.Key` (`IntelligenceStoreConsumer.cs:216`), id = `KeyToUlong(key)` (`:100`) — but `KeyToUlong` is `private` (`:564`). Addressed in §8. |
| A5 | Metadata is denormalized onto chunk points | ✅ `IntelligenceStoreConsumer.cs:223-235`, typed via `ExtractTypedValue` with the column's `SqlType`, camelCase keys |
| A6 | Object points carry declared timestamp metadata | ✅ `BuildObjectPointPayload:340-348` writes all `ScalarColumns` (a superset of `MetadataColumns`) |
| A7 | Metadata fields are typed at search time | ✅ `MetadataColumns` (names) joined to `ScalarColumns[].SqlType`; `ClrDatetime → TIMESTAMPTZ/DATETIME` — `SchemaBuilder.cs:243` |
| A8 | Timestamps are parseable in the payload | ❌ **Not as designed.** `ExtractTypedValue` has no timestamp case; `TIMESTAMPTZ` falls to the default at `IntelligenceStoreConsumer.cs:626` returning `v.GetString()`, so `ToQdrantValue`'s `"o"` branch (`IntelligenceVectorService.cs:184-185`) is unreachable from ingest and the stored format is client-determined. Addressed in §8. |
| A9 | `System.Numerics.Tensors` is usable | ✅ not referenced today; `10.0.10` restores cleanly for `net10.0` in a scratch project. No central package management in this repo. |
| A10 | `top_k` is not clamped, so 4× over-fetch survives | ✅ only `Math.Max(1, …)` — `ObjectSearchGrpcService.cs:194`, `:296` |
| A11 | Both RPCs can resolve the object collection | ✅ `ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks: false)`; both have `schema` and `decision.TenantValue` (`:195`, `:294`) |
| A12 | A second read-only scoped api-key is mintable per request | ✅ `MintScopedApiKey(collection, readOnly: true)` inside `RequestHeaders.Use` — `:198`, `:300`; a second sequential scope is the same pattern |
| A13 | Qdrant returns cosine similarity, not distance | ✅ `Distance.Cosine` — `IntelligenceCollectionManager.cs:35`, `:230` |
| A14 | The `_centroid` name is derivable from the request property | ✅ `ToSnakeCase() + "_centroid"` matches `SchemaBuilder.cs:219`; the service already builds `_vector` names the same way (`:193`, `:293`) |
| A15 | Nothing depends on current score semantics or ordering | ✅ exactly one score assertion exists (`ObjectSearchGrpcServiceTests.cs:1086`, `BeApproximately(0.95f)`); no ordering assertions; all five client SDKs pass `score` through untouched; no MCP consumer |
| A16 | The design holds for **both** RPC paths individually | ❌ **Broke the design.** Centroids are named after chunk fields (`SchemaBuilder.cs:219`) but `SearchSimilar`'s property is an `[IversonEmbedding]` field, and `IsEmbedding`/`IsChunk` are independent (`SchemaBuilder.cs:59`, `:66`), so `<embeddingField>_centroid` usually does not exist. Resolved in §5. |
| A17 | The Qdrant client can retrieve points by id with named vectors (§4 depends on it; A3 covers only our own interface's lack of a method) | ✅ batched `RetrieveAsync(string, IReadOnlyList<PointId>, WithPayloadSelector, WithVectorsSelector, …)` exists, and `WithVectorsSelector.op_Implicit(String[])` permits selecting `["<field>_centroid"]` specifically — `Qdrant.Client.xml` (1.18.1) |
| A18 | `System.Numerics.Tensors` exposes the operation the design needs (A9 verified only that the package restores) | ✅ `TensorPrimitives.CosineSimilarity` present — `System.Numerics.Tensors.xml` (10.0.10) |
| A19 | The response contract can carry the fused score | ✅ `float score` on both `SearchResponse` (`object_search.proto:84`) and `ChunkSearchResponse` (`:121`) |
| A20 | `ExtractTypedValue` feeds only Qdrant payload construction, so §8's normalization cannot alter Postgres or StarRocks writes | ✅ three call sites, all in `IntelligenceStoreConsumer` — chunk metadata loop (`:233`), `BuildObjectPointPayload` scalar loop (`:346`), FK loop (`:351`, hard-coded `"TEXT"`) |
| A21 | Exactly three filter operators can carry a timestamp operand into a payload string comparison, bounding §8's read-side rule | ✅ `EQUALS`/`NOT_EQUALS` → `BuildEqualityCondition` → `MatchKeyword`, `IN` → `Conditions.Match(list)` (`IntelligenceFilterBuilder.cs:73-79`, `:105`); the four range operators call `RequireNumber` (`:75-78`) and reject a non-`NumberVal` value |

A16 and the absent-signal penalty it exposed were re-approved by Ben before this spec was
written; §2 and §5 record the outcomes.

## Out of scope

- **Keyword and summary signals.** Considered and excluded; the fused score uses base cosine,
  centroid and decay only.
- **Caller-supplied weights or a per-request opt-out.** The weights are fixed server-side
  constants by decision.
- **The DSL `Search` path and `VECTOR_SIMILAR` clauses.** Re-ranking applies to `SearchChunks`
  and `SearchSimilar` only.
- **Part 4b cluster centroids** and **part 5's agent-facing surface.** Their own specs.
- **Backfilling centroids onto pre-4a documents.** They re-rank without the centroid signal
  until republished; §2's renormalization is what makes that safe.

## Known issues accepted

**Re-ranking is unconditional and changes `score` semantics for every existing caller** of the
two RPCs. Accepted by Ben as a consequence of choosing fixed server-side constants over
per-request knobs; §2 records the reasoning.

**A `top_k = N` request costs a `4N` Qdrant fetch** with no ceiling. Accepted deliberately —
see §3.
