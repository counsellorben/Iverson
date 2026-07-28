# Derived Vector Signals — Per-Object Chunk Centroids Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-28-derived-vector-signals-design.md` (commit SHA: `1bd2d5d`)

**Goal:** Compute one derived vector per chunked field — the mean of that field's chunk vectors — and store it as `<field>_centroid` on the object point, so part 3's re-rank has a document-level signal to score against.

**Architecture:** `SchemaBuilder.ToCollectionSchema` declares a `_centroid` named vector per chunk field on the object collection. `IntelligenceStoreConsumer` computes each centroid from the chunk vectors it already has in hand, then writes them to the object point after the chunk loop — updating the point in place when the object block wrote one this event, and creating it otherwise.

**Tech stack:** .NET 10, Qdrant.Client 1.18.1 (`UpdateVectorsAsync`, `PointVectors`, `NamedVectors`), xUnit + NSubstitute + FluentAssertions.

---

## Global Constraints

Copied from the spec; every task must hold to these.

- **Server-side only.** No proto change, no codegen re-run, no client change. `SchemaBuilder` and `IntelligenceStoreConsumer` are the only places that need to know this feature exists.
- **Automatic for every chunk field.** No declaration mechanism, no opt-in annotation, no kill switch.
- **No zero-magnitude guard in the mean.** The spec establishes that no path admits a zero vector: blank text is skipped at `:199` and `SplitIntoChunks` yields ≥1 chunk from non-blank text. Adding a guard would be speculative.
- **The write branches on whether an object point was actually written this event**, never on `VectorFields.Count`. See assumption 16 in the spec.

## File Structure

**Modify**
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — `ToCollectionSchema` gains the centroid projection.
- `Iverson.Server/Iverson.Vector/IVectorRoles.cs` — `IVectorWriteService` gains `UpdateNamedVectorsAsync`.
- `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs` — implement it over Qdrant's `UpdateVectorsAsync`.
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — mean helper, payload-helper extraction, written-flag, centroid write.

**Test**
- `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`
- `Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs`
- `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and **not** re-verified here (spec's 1–17). The load-bearing ones for this plan:

- **1–2:** `ToCollectionSchema` derives object named vectors from `VectorFields` only; `ChunkDescriptor.Dimension` comes from `embedding.Dimension`, so the centroid's declared dimension matches the vector length by construction.
- **3:** `_centroid` cannot collide with `_vector`.
- **5–6:** Qdrant's update keeps unspecified vectors unchanged, and requires that all given points exist — which is why the upsert branch is mandatory.
- **8:** `IVectorWriteService` has one production implementer.
- **9:** `pointId`, `ownerField`, `authoritativeOwnerValue` and `authoritativeTenantValue` are method-scoped and live after the chunk block ends at `:261`.
- **10–12:** nothing depends on the object collection's vector set equalling `VectorFields`; client search cannot name an arbitrary vector; no test asserts `ToCollectionSchema`'s vector list.
- **16–17:** the object write sits inside `if (namedVectors.Count > 0)` (`:138`), not merely `:119`; Qdrant's upsert nulls unspecified vectors, so the object upsert clears centroids and the centroid write restores them within the same event.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | All four Modify targets and all three Test files exist at the cited paths | `[ -f ]` on all seven — all present |
| P2 | Command | Both test projects exist as named | `Iverson.Api.Tests/Iverson.Api.Tests.csproj`, `Iverson.Vector.Tests/Iverson.Vector.Tests.csproj`; `Iverson.Server.slnx` present |
| P3 | Command | Conventional-Commit scopes `feat(schema)`, `feat(vector)`, `feat(consumer)` are established | `git log --all` grep: one existing commit each, plus `fix(schema)` ×3, `fix(vector)`, `test(vector)` ×2 |
| P4 | Signature | `QdrantClient.UpdateVectorsAsync(string collectionName, IReadOnlyList<PointVectors> points, bool wait = true, …)` | `Qdrant.Client.xml`, `M:Qdrant.Client.QdrantClient.UpdateVectorsAsync` |
| P5 | Signature | `PointVectors` exposes `Id` and `Vectors` | `Qdrant.Client.xml`, `P:Qdrant.Client.Grpc.PointVectors.{Id,Vectors}` |
| P6 | Signature | `UpsertNamedAsync` builds `NamedVectors` then wraps in `new Vectors { Vectors_ = named }`, under a `Telemetry.Source.StartActivity("qdrant.upsert_named", …)` with `db.system` / `qdrant.collection` / `qdrant.point_id` / `qdrant.vector_count` tags — mirrorable for the update | `IntelligenceVectorService.cs:38-57` |
| P7 | Signature | `chunkResults` elements carry the vector as `chunkVector` | `IntelligenceStoreConsumer.cs:219` returns `(chunkVector, chunkId, chunkText, chunkIndex)`; destructured at `:224` |
| P8 | Signature | `ToSnakeCase()` is in scope in the consumer | used at `:127` and `:201` |
| P9 | Signature | `EnsureCollectionAsync(CollectionSchema)` is a private method on the consumer | `IntelligenceStoreConsumer.cs:404` |
| P10 | Signature | The `RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collection, readOnly: false))` pattern is reusable for a second object-collection write | four existing uses: `:163`, `:194`, `:301`, `:310` |
| P11 | Code validity | `internal` members of `Iverson.Api` are visible to `Iverson.Api.Tests`, and `Iverson.Vector`'s to `Iverson.Vector.Tests`, so the mean can be unit-tested directly rather than by reflection | `InternalsVisibleTo` declared via `AssemblyAttribute` in both `Iverson.Api.csproj:10-13` and `Iverson.Vector.csproj:10`; the precedent is documented at `IntelligenceVectorService.cs:140` |
| P12 | Code validity | `IntelligenceStoreConsumer` is a `public sealed class` with a primary constructor, so an added `internal static` method is legal and testable | `IntelligenceStoreConsumer.cs:26` |
| P13 | Ordering | Task 3 consumes Tasks 1 and 2; Tasks 1 and 2 touch disjoint files and share no symbols | T1: `SchemaBuilder.cs` + its test. T2: `IVectorRoles.cs`, `IntelligenceVectorService.cs` + its test. No overlap |
| P14 | Consumer impact | Adding a method to `IVectorWriteService` breaks no implementer: exactly one production class implements it, and no hand-rolled test fake exists — the test doubles are `Substitute.For<>` | `grep IVectorWriteService` excluding `Substitute.For`: only `IntelligenceVectorService.cs:8`, the DI registration at `ServiceCollectionExtensions.cs:42`, the consumer's ctor param, one field declaration, and a type-implements assertion at `QdrantVectorServiceTests.cs:197` |
| P15 | Consumer impact | `ToCollectionSchema` has exactly two callers, and widening its vector list breaks neither | `IntelligenceStoreConsumer.cs:161` (passes it straight to `EnsureCollectionAsync`) and `SchemaBuilderTests.cs:283` (asserts payload index names only) |
| P16 | Consumer impact | The object payload block is `:138-158` — the dictionary initialisation through the FK loop, stopping **before** `ResolveCollectionName` at `:159`. The spec cites `:139-158`, which omits the initialisation line; this plan uses the precise range. It captures only `ev.Key`, `schema`, `payload`, `ownerField` and `authoritativeOwnerValue`, and calls only pre-existing helpers (`ExtractString`, `ExtractTypedValue`, `ToCamelCase`) | Read of `IntelligenceStoreConsumer.cs:138-159` |
| P17 | Consumer impact | The payload helper degrades correctly on both new branches: its first loop iterates `schema.VectorFields` copying non-blank field text, which no-ops for a chunks-only entity (empty collection) and for the all-blank case (every text fails the `IsNullOrWhiteSpace` guard) | `IntelligenceStoreConsumer.cs:139-144` |
| P18 | Signature | `ev` (`:72`) and `payload` (`:88`) are declared directly in `HandleAsync`'s body, so both are live at Step 5's payload-helper call site after the chunk block closes at `:261` | Read of `IntelligenceStoreConsumer.cs:72`, `:88` |

## Tasks

### Task 1: Collection schema carries a centroid per chunk field

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:217-223`
- Test: `Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`

**Interfaces:**
- Produces: object collections that declare `<field>_centroid`. Task 3's writes fail without this — Qdrant rejects a vector name the collection does not declare.

- [ ] **Step 1: Add the centroid projection**

In `ToCollectionSchema`, concatenate a `ChunkFields` projection onto the existing `VectorFields` one:

```csharp
d.VectorFields.Select(v => new NamedVector($"{v.PropertyName.ToSnakeCase()}_vector", v.Dimension))
    .Concat(d.ChunkFields.Select(c => new NamedVector($"{c.PropertyName.ToSnakeCase()}_centroid", c.Dimension)))
    .ToList(),
```

Leave `ToChunkCollectionSchema` (`:205-215`) alone — the chunk collection is unchanged.

- [ ] **Step 2: Test it**

Add a `SchemaBuilderTests` case building a descriptor with at least one vector field and one chunk field, asserting the object collection schema carries both the `_vector` entry and a `_centroid` entry named from the chunk field at that field's dimension. No existing test asserts this vector list (P15), so this is additive.

- [ ] **Step 3: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 4: Commit**
```bash
git add Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs Iverson.Server/Iverson.Api.Tests/Schema/SchemaBuilderTests.cs
git commit -m "feat(schema): declare a centroid vector per chunk field on the object collection"
```

---

### Task 2: `UpdateNamedVectorsAsync` on the vector service

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IVectorRoles.cs`
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs`
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs`

**Interfaces:**
- Produces: `UpdateNamedVectorsAsync`, which Task 3 calls. Independent of Task 1.

- [ ] **Step 1: Add the interface method**

On `IVectorWriteService`, beside `UpsertNamedAsync`:

```csharp
Task UpdateNamedVectorsAsync(
    string collectionName,
    ulong id,
    IReadOnlyDictionary<string, float[]> namedVectors);
```

Adding it breaks no implementer (P14).

- [ ] **Step 2: Implement it**

On `IntelligenceVectorService`, mirroring `UpsertNamedAsync` (`:33-58`): open a `Telemetry.Source.StartActivity("qdrant.update_named_vectors", ActivityKind.Client)` and set the same four tags (`db.system`, `qdrant.collection`, `qdrant.point_id`, `qdrant.vector_count`); build `NamedVectors` the same way; then

```csharp
var point = new PointVectors
{
    Id      = id,
    Vectors = new Vectors { Vectors_ = named }
};

await client.UpdateVectorsAsync(collectionName, [point]);
```

`UpdateVectorsAsync` takes `(string, IReadOnlyList<PointVectors>, …)` with `wait` defaulting to true (P4), so the list form matches the existing `UpsertAsync(collectionName, [point])` call shape.

- [ ] **Step 3: Test it**

In `QdrantVectorServiceTests`, follow the file's existing convention for the `UpsertNamedAsync` coverage. Assert the method is on the interface, matching the existing type-implements assertion at `:197`.

- [ ] **Step 4: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Vector/IVectorRoles.cs Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs Iverson.Server/Iverson.Vector.Tests/QdrantVectorServiceTests.cs
git commit -m "feat(vector): add UpdateNamedVectorsAsync for partial named-vector updates"
```

---

### Task 3: Consumer computes and writes the centroids

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

**Interfaces:**
- Consumes: Task 1's centroid projection (the collection must declare the vectors) and Task 2's `UpdateNamedVectorsAsync`.

- [ ] **Step 1: Add the mean**

Add to `IntelligenceStoreConsumer`:

```csharp
internal static float[] ComputeCentroid(IReadOnlyList<float[]> vectors)
```

L2-normalize each input vector, average componentwise, return **without** re-normalizing — Qdrant normalizes on store (`Distance.Cosine`) and cosine is scale-invariant, so a second normalization buys nothing. `internal` rather than `private` so the test project reaches it directly (P11), matching the precedent at `IntelligenceVectorService.cs:140` rather than testing by reflection.

Per Global Constraints, do **not** add a zero-magnitude guard.

Unit tests in `IntelligenceStoreConsumerTests`: a known-vector fixture with an asserted expected centroid, and the single-chunk case where the result is that chunk normalized.

- [ ] **Step 2: Extract the payload helper**

Move `:138-158` — the `pointPayload` dictionary initialisation through the FK loop, stopping before `ResolveCollectionName` at `:159` — into a private method. Its parameters are exactly what it captures (P16): `ev.Key`, `schema`, `payload`, `ownerField`, `authoritativeOwnerValue`. Call it from the existing object block in place of the moved code.

- [ ] **Step 3: Add the written flag**

Declare `var objectPointWritten = false;` before the object block at `:119`, and set it `true` immediately after the `UpsertNamedAsync` at `:163` returns. This is the predicate the centroid write branches on — never `VectorFields.Count` (Global Constraints; spec assumption 16).

- [ ] **Step 4: Accumulate the centroids**

Declare `var centroids = new Dictionary<string, float[]>();` before the chunk block at `:173`. Inside the per-field loop, after `chunkResults` is awaited (`:222`), compute the centroid from `chunkResults`' `chunkVector` members (P7) and store it under `$"{cf.PropertyName.ToSnakeCase()}_centroid"` — the same name Task 1 declares.

A blank field already `continue`s at `:199` and so contributes no entry, which is what leaves its named vector absent rather than zeroed.

- [ ] **Step 5: Write the centroids**

After the chunk block closes at `:261`, when `centroids.Count > 0` and `authoritativeTenantValue is not null`:

1. resolve the object collection name via `tenantScope.ResolveCollectionName(schema.CollectionName, authoritativeTenantValue, isChunks: false)`;
2. `await EnsureCollectionAsync(SchemaBuilder.ToCollectionSchema(schema) with { CollectionName = collectionName })` — the object block may not have run, so this path ensures the collection itself;
3. inside `using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collectionName, readOnly: false)))` (P10), branch:
   - `objectPointWritten` → `await vectorWrite.UpdateNamedVectorsAsync(collectionName, pointId, centroids);`
   - otherwise → `await vectorWrite.UpsertNamedAsync(collectionName, pointId, centroids, <payload helper>);`

- [ ] **Step 6: Consumer tests**

Four cases in `IntelligenceStoreConsumerTests`:

- an entity with both an embedding field and a chunk field: `UpdateNamedVectorsAsync` received for the object collection with the expected `_centroid` key;
- a chunks-only entity: `UpsertNamedAsync` received for the object collection carrying the centroid and the payload;
- a blank chunk field: no centroid key written;
- the update path does not clobber: for the both-fields case, `Received(1)` on `UpsertNamedAsync` for the object collection — the object block's own call at `:163`, unchanged — together with `Received(1)` on `UpdateNamedVectorsAsync(objectCollection, pointId, …)`. That pair is what proves the centroid write added an update rather than a second, clobbering upsert. Do **not** assert `DidNotReceive()` on `UpsertNamedAsync` here: the object block calls it on this path, so the assertion would fail. For the chunks-only case the mirror applies — `Received(1)` on `UpsertNamedAsync` for the object collection and `DidNotReceive()` on `UpdateNamedVectorsAsync` — proving branch selection in the other direction.

- [ ] **Step 7: Build and test**
```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "feat(consumer): compute and write per-object chunk centroids"
```

## Tasks NOT in this plan

Inherited from the spec's Out of scope. A new spec → plan cycle is required to add any of these.

- **4b, cross-corpus cluster centroids.** Its own spec.
- **Part 3's consumption of these signals.** This spec produces the vectors; nothing reads them yet. That is deliberate — 4 precedes 3 in the initiative's order so the re-rank has signals to score over.
- **Exposing centroids to client-facing search.** Vector names are server-derived; making centroids client-selectable is a separate decision.
- **Token-weighted or otherwise tunable centroids.** Considered and rejected.
- **A kill switch for centroid computation.** Not requested; the write cost is small relative to the chunk points already being written.

## Known issues inherited from spec

**A centroid can outlive the text it summarizes on the paths that do not rewrite the object point.** When a chunk field is blank, no centroid is computed and none is written: on the paths that rewrite the object point the previous centroid is dropped, because Qdrant's upsert nulls unspecified vectors; on the paths that do not — a chunks-only entity, or one whose declared vector fields are all blank — the previous centroid persists until an event repopulates the field. Accepted deliberately by Ben during the second design review: the same is already true of the chunk points themselves, which `:199` leaves in place when a field is cleared, so centroids are no staler than the passages they summarize.

**The object point exists without its centroids if the process crashes between the two writes**, until the next republish. Accepted: it matches behaviour part 2 already has, where chunk prefixes are regenerated non-deterministically on every republish.
