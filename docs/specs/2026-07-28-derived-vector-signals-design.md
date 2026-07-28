# Derived Vector Signals — Per-Object Chunk Centroids

**Sub-project 4a of the metadata / tensor-search initiative**

The initiative adds metadata to Iverson plus `System.Numerics.Tensors` server-side
re-ranking, to improve semantic search and cut downstream LLM token spend. Its parts are
(1) metadata foundation, (2) Ollama ingest enrichment, (3) tensor re-ranking/fusion,
(4) derived vector signals, and (5) an agent-facing schema surface. Parts 1 and 2 are
merged. This spec covers **part 4a only**.

Part 4 was split during brainstorming. "Derived vector signals (centroids/clusters)"
covers two distinct things:

- **4a — per-object centroids** (this spec): one derived vector per chunked field, the
  mean of that object's own chunk vectors. Deterministic, computed at ingest from vectors
  already in hand, no recomputation strategy.
- **4b — cross-corpus cluster centroids** (deferred to its own spec): k centroids across a
  type/tenant. Needs a batch job, a choice of k, a recomputation trigger and a staleness
  policy, because every ingest shifts the clusters. 4b may reuse 4a's output as input.

## Problem

Chunked fields are split into passages and embedded per passage, so vector search over
them answers "which passages match" but never "which documents are about this." A long
document has no whole-document vector: `[IversonEmbedding]` embeds a field's full text in
one shot, which is exactly what a long field cannot do. Part 3's re-rank needs a
document-level signal to score against alongside passage hits, and nothing produces one.

## Design

### 1. The signal

For every field declared as a chunk field, the server computes one derived vector: the
**L2-normalized mean of that field's chunk vectors**, stored as a named vector
`<field>_centroid` on the object point, beside the existing `<field>_vector` embeddings.

One centroid per chunk field, not one per object. This mirrors the existing
one-named-vector-per-chunk-field structure (`SchemaBuilder.cs:213`) and keeps each centroid
in the same embedding space as the chunks it summarizes, so cosine against it is
meaningful. A single centroid averaged across several chunk fields would blend distinct
semantics into one point, and would be ill-defined if the fields ever used different
models or dimensions.

Each chunk vector is L2-normalized before averaging, so every chunk contributes equally
regardless of its embedding's magnitude — the usual meaning of "centroid" in a cosine
space. A plain mean of raw vectors would let high-magnitude chunks dominate, a weighting
nobody chose and which varies per document. Qdrant normalizes the stored result itself,
since both collection paths use `Distance.Cosine` (`IntelligenceCollectionManager.cs:35`,
`:230`). Token-count weighting was considered and rejected: it is a tuning decision with no
obvious right answer, and calibration cannot repair a mechanism nobody validated.

Centroids are computed for every chunk field automatically. No declaration, no proto
change, no client change — `SchemaBuilder` and `IntelligenceStoreConsumer` are the only
places that need to know this feature exists. The cost is one extra vector per chunked
field per object — at the current model's 768 dimensions, roughly 3KB — which is small
beside the chunk points already being written for the same field.

### 2. Collection schema

`SchemaBuilder.ToCollectionSchema` (`:217`) currently derives the object collection's named
vectors from `VectorFields` alone. It gains a second source:

```csharp
d.ChunkFields.Select(c => new NamedVector($"{c.PropertyName.ToSnakeCase()}_centroid", c.Dimension))
```

concatenated with the existing `VectorFields` projection.

The dimension comes from `ChunkDescriptor.Dimension`, which `SchemaBuilder` populates from
`embedding.Dimension` (`:74`) — the same `EmbeddingService` that produces the chunk vectors
at ingest. The centroid's declared dimension therefore matches the vector length by
construction rather than by coincidence.

The `_centroid` and `_vector` suffixes are disjoint, so a centroid name cannot collide with
an embedding field's vector name, and property names are unique within a type.

### 3. Write path

The centroid can only be computed once `chunkResults` exists
(`IntelligenceStoreConsumer.cs:222`), which is 57 lines after the object point is already
written (`:165`). Rather than reorder the consumer, the centroid write is **appended** as a
second write after the chunk loop.

The alternative — splitting the chunk block into compute and write phases so a single
object upsert carries both embeddings and centroids — yields atomic points and needs no
partial-update semantics. It was rejected because it restructures the most
carefully-built block in the consumer: the contextual-prefix summary fetch, the
`SemaphoreSlim` fan-out cap added by part 2's final review, and the per-field blank-text
`continue`. A regression there is a regression in enrichment, not merely in centroids.

`IVectorWriteService` gains one method beside `UpsertNamedAsync`:

```csharp
Task UpdateNamedVectorsAsync(
    string collectionName,
    ulong id,
    IReadOnlyDictionary<string, float[]> namedVectors);
```

implemented on `IntelligenceVectorService` over Qdrant's `UpdateVectorsAsync`, following
the existing method's shape — same `Telemetry.Source.StartActivity` pattern, same
`NamedVectors` construction. Qdrant's documented semantics are that unspecified vectors are
kept unchanged, so the object's own embeddings and payload survive the update.

After the chunk loop, if any centroids were computed:

- **the object block ran** (`VectorFields.Count > 0`) → `UpdateNamedVectorsAsync`;
- **it did not** (a chunks-only entity) → ensure the object collection and
  `UpsertNamedAsync` a new point carrying the centroids plus the object payload.

The second branch is mandatory, not a nicety. Qdrant's update requires that all given
points exist, so an update against a chunks-only entity would error outright. Such entities
are reachable: `StoreTargeting.cs:42-43` routes to the Intelligence store when there are
vector fields **or** chunk fields, but the object-point write is gated on
`VectorFields.Count > 0` (`:119`), so an entity with a chunked `Body` and no
`[IversonEmbedding]` field has no object point today.

To serve both branches, the object payload construction at `:139-158` moves into a private
helper. Both paths sit under the existing `authoritativeTenantValue is not null` condition
and mint a scoped API key for the object collection, matching `:159-166`.
`pointId` (`:99`), `ownerField` (`:104`), `authoritativeOwnerValue` (`:105`) and
`authoritativeTenantValue` (`:114`) are all method-scoped and remain live after the chunk
block closes at `:261`.

### 4. Edge cases

- **Blank chunk field** — already hits `continue` (`:199`) and produces no chunks. It gets
  no centroid, and the named vector is simply absent from the point rather than written as
  zeros, which would be a false neighbour for every query.
- **Single chunk** — the centroid equals that chunk normalized. Correct; no special case.
- **Empty chunk set** — cannot arise. `SplitIntoChunks` loops `while (start < text.Length)`
  (`:421`), so non-blank text always yields at least one chunk.
- **Re-ingest** — recomputes from the full field text, so the centroid always reflects the
  current document rather than accumulating.
- **Crash between the two writes** — the object point exists without its centroids until
  the next republish. Accepted: it matches behaviour part 2 already has, where chunk
  prefixes are regenerated non-deterministically on every republish.

### 5. Migration

No new collection, no proto change, no client change. Existing collections acquire the
centroid vectors through the alias migration already in `IntelligenceCollectionManager`,
which detects missing named vectors (`:66-77`), creates a new physical collection, copies
the points (`:115-120`) and repoints the alias. A dimension mismatch on an existing vector
throws rather than corrupting the collection — pre-existing behaviour that applies to
centroids identically.

### 6. Testing

- **Mean** — normalize-then-average against a known-vector fixture with an asserted
  expected centroid; the single-chunk identity case.
- **`SchemaBuilder`** — the object collection schema carries one `_centroid` per chunk
  field at that field's dimension, alongside the existing `_vector` entries.
- **Consumer** — centroids written for an entity with both embedding and chunk fields;
  centroids written for a chunks-only entity via the upsert branch; no centroid for a blank
  chunk field; and **the update path leaves the object's existing embeddings and payload
  intact** — the assertion that catches a regression to a clobbering upsert.

## Verified assumptions

Checked against the codebase at `main@24a7fef` before this spec was written.

1. `ToCollectionSchema` (`SchemaBuilder.cs:217-223`) derives object named vectors from
   `VectorFields` only; `ChunkFields` can be concatenated there.
2. `ChunkDescriptor` carries `Dimension` (`SchemaDescriptor.cs:57-59`), set from
   `embedding.Dimension` (`SchemaBuilder.cs:74`) — the same service that embeds chunks at
   ingest, so declared and actual dimensions match by construction.
3. `_centroid` cannot collide with `_vector`; the suffixes are disjoint and property names
   are unique per type.
4. Qdrant.Client 1.18.1 exposes `UpdateVectorsAsync`, `PointVectors` and `NamedVectors`
   (verified in the packaged assembly).
5. Qdrant's update-vectors operation keeps unspecified vectors unchanged, so other named
   vectors and the payload survive (Qdrant documentation, "Update vectors").
6. The same operation requires that all given points exist, so it errors on a missing
   point — which is why the chunks-only upsert branch is mandatory.
7. The alias migration detects missing named vectors (`IntelligenceCollectionManager.cs:66-77`)
   and copies existing points (`:115-120`).
8. `IVectorWriteService` has exactly one production implementer,
   `IntelligenceVectorService` (`:8`), registered at `ServiceCollectionExtensions.cs:42`.
   Every other reference is an NSubstitute mock.
9. `pointId` (`:99`), `ownerField` (`:104`), `authoritativeOwnerValue` (`:105`) and
   `authoritativeTenantValue` (`:114`) are method-scoped and live after the chunk block
   ends at `:261`; the `:139-158` payload block depends only on those plus `payload`,
   `schema` and `ev.Key`, so it is extractable.
10. **Nothing else depends on the object collection's named-vector set equalling
    `VectorFields`.** `IntelligenceCollectionManager` is the only file in the repo that
    reads collection vector config, and its loops are generic over `schema.Vectors`. The
    Admin UI has no references to named vectors.
11. Client-facing search cannot name an arbitrary vector: `ObjectSearchGrpcService.cs:193`
    derives `vectorName` server-side as `<property>_vector` from a resolved descriptor.
    Adding centroids therefore does not expose them to client search or alter existing
    search behaviour.
12. No test asserts `ToCollectionSchema`'s vector list; the existing test at
    `SchemaBuilderTests.cs:279` asserts payload index names only.
13. `SplitIntoChunks` (`:412-423`) yields at least one chunk for non-blank text; blank text
    is skipped at `:199`.
14. Chunk fields drive no SQL or StarRocks DDL. `RowFieldAuthorizationEvaluator.cs:74` uses
    their property names for field authorization, which adding a named vector does not
    affect.
15. `StoreTargeting.cs:42-43` routes to the Intelligence store on vector **or** chunk
    fields, so chunks-only entities reach the consumer.

## Out of scope

- **4b, cross-corpus cluster centroids.** Its own spec.
- **Part 3's consumption of these signals.** This spec produces the vectors; nothing reads
  them yet. That is deliberate — 4 precedes 3 in the initiative's order so the re-rank has
  signals to score over.
- **Exposing centroids to client-facing search.** Vector names are server-derived; making
  centroids client-selectable is a separate decision.
- **Token-weighted or otherwise tunable centroids.** Considered and rejected above.
- **A kill switch for centroid computation.** Not requested; the write cost is small
  relative to the chunk points already being written.
