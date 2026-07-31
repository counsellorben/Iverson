# Result Diversification (MMR) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-30-result-diversification-design.md` (commit SHA: `b3d2cca`)

**Goal:** Stop `SearchChunks` and `SearchSimilar` from returning a `top_k` whose entries are topically redundant, by replacing part 3's `Take(topK)` with greedy maximal marginal relevance over the candidate pool part 3 already over-fetches.

**Architecture:** A pure, I/O-free `IResultDiversifier` in `Iverson.Vector`, beside `ResultReranker`, selects `topK` from the fused-descending candidate list by maximising `λ·fused(c) − (1−λ)·maxSim(c)` with `λ = 0.70` and an incrementally-maintained `maxSim`. Each RPC supplies the diversity vector at the granularity of what it returns: `SearchSimilar` reuses the object centroid part 3 already holds (no new I/O); `SearchChunks` issues one additional retrieve against the **chunks** collection for `<property>_vector`, because the parent centroid is the wrong granularity for passage entries.

**Tech stack:** .NET 10, `System.Numerics.Tensors` 10.0.10 (`TensorPrimitives.CosineSimilarity`), Qdrant.Client 1.18.1, xUnit 2.9.3 + NSubstitute 5.3.0 + FluentAssertions 7.0.0.

---

## Global Constraints

Copied from the spec; every task must hold to these.

- **λ = 0.70, a fixed server-side constant.** No caller-supplied weights, no per-request opt-out, no kill switch, no configuration knob.
- **The highest-fused candidate is always selected first**, unconditionally, before any similarity work.
- **Ties break toward the earlier candidate in fused-descending order.** This is what makes the degradation guarantee exact rather than approximate.
- **`maxSim` is computed incrementally** — each remaining candidate carries a running maximum updated against only the newly-selected candidate, costing `topK × poolSize` similarity computations, not `topK² × poolSize`.
- **An absent signal is never replaced by a substituted value.** Either vector absent → no similarity term for that pair and no penalty; such a candidate scores `λ · fused(c)`.
- **With every diversity vector absent, the selected set and its order are identical to today's `Take(topK)` bit-for-bit.**
- **Two numeric hazards need *different* treatment** (spec A3, established empirically): differing vector lengths make `TensorPrimitives.CosineSimilarity` **throw `ArgumentException`**, so length equality is checked *before* the call; a zero-magnitude vector makes it return **`NaN`**, which is treated as absent. A `NaN` reaching a naive forward argmax would be selected when it appears first.
- **Failures degrade, never throw.** A failed chunk-vector retrieve logs and continues with every diversity vector absent; a failed diversification must never fail a search.
- **The diversity vector is supplied per candidate, not derived.** The diversifier is ignorant of chunk-versus-object semantics.
- **Not folded into `IResultReranker`.** Fusion and selection stay separate; part 3's bit-exactness guarantee and its tests are untouched. The score returned to the client remains the fused score.
- **Part 3's over-fetch gate is not changed** and gains no new condition.
- **Scope is `SearchChunks` and `SearchSimilar` only** — not the DSL `Search` path, not `VECTOR_SIMILAR` clauses.

## File Structure

**Create**
- `Iverson.Server/Iverson.Vector/IResultDiversifier.cs` — the selection contract and its candidate record.
- `Iverson.Server/Iverson.Vector/ResultDiversifier.cs` — greedy MMR over `TensorPrimitives.CosineSimilarity`.
- `Iverson.Server/Iverson.Vector.Tests/ResultDiversifierTests.cs` — formula and selection tests.

**Modify**
- `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs` — register the diversifier beside the re-ranker.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — inject the diversifier; replace `.Take((int)topK)` on both RPCs; add the chunk-vector retrieve on `SearchChunks`.
- `Iverson.Clients/Common/Proto/object_search.proto` — record the ordering change on both `score` fields.

**Test**
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — service-level wiring for both RPCs, plus the mandated update to `SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds`.

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time (spec's A1–A12) and **not** re-verified here. The load-bearing ones:

- **A1:** `Rerank` returns `RerankedResult(ulong Id, double FusedScore)` sorted fused-descending; both RPCs trim with `Take((int)topK)` at `ObjectSearchGrpcService.cs:254` and `:395`.
- **A2:** the fused score is a weighted mean of quantities in `[0,1]`, so it shares a numeric scale with cosine similarity and one λ blend is meaningful.
- **A3 / A3b:** `CosineSimilarity` returns `NaN` for a zero-magnitude vector but **throws `ArgumentException`** for differing lengths and for two empty spans; a `NaN` reaching a naive forward argmax is selected when it appears first, while LINQ `MaxBy` treats `NaN` as smallest.
- **A4 / A5:** `SearchChunks` already resolves each candidate's *parent* centroid; `SearchSimilar` already resolves each candidate's *own* centroid keyed by candidate id.
- **A7:** part 3's over-fetch gate remains correct with MMR added — MMR needs `centroidPossible`, which already forces the `4 ×` branch, and `SearchChunks` never gates.
- **A9:** every test reaching the selection step was checked. Eight of nine pass unchanged; `SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds` does not, because its stub matches `Arg.Any<string>()` for the collection and so intercepts both retrieves.
- **A11:** chunk points carry a named vector `<property>_vector` in the chunks collection, retrievable by point id — the same name `SearchChunks` already passes to `SearchNamedAsync`.
- **A12:** a chunk vector encodes the bare passage for a non-contextual field, and the passage plus a document-shared context prefix for a contextual one.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `main@68c46c4`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | The three created files do not already exist | `ls` of `Iverson.Vector/IResultDiversifier.cs`, `Iverson.Vector/ResultDiversifier.cs`, `Iverson.Vector.Tests/ResultDiversifierTests.cs` — all "No such file or directory" |
| P2 | File path | The modified/test files exist at exactly these paths | `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`, `Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj`, `Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj` all listed |
| P3 | File path | `Iverson.Clients/Common/Proto/object_search.proto` is the only proto source; the Java `target/classes` copy is build output, and a comment-only edit needs **no** client regeneration | `find` returns those two paths only; commit `5834131` added the existing `score` comments touching **only** `Common/Proto/object_search.proto` — no generated client artifact was regenerated in that commit |
| P4 | Signature | `TensorPrimitives.CosineSimilarity(ReadOnlySpan<float>, ReadOnlySpan<float>)` returns **`float`**, and `float[]` binds to it implicitly | `System.Numerics.Tensors.xml:145` (10.0.10, net10.0) declares `CosineSimilarity(System.ReadOnlySpan{System.Single},System.ReadOnlySpan{System.Single})`; `ResultReranker.cs:40` calls it with two `float[]`. The MMR expression therefore mixes a `float` cosine with a `double` fused score — assign the cosine to a `double` (implicit widening) and test it with `float.IsNaN`/`double.IsNaN` on the widened value |
| P5 | Signature | `IVectorQueryService.RetrieveNamedVectorAsync(string collectionName, IReadOnlyList<ulong> ids, string vectorName)` returns `Task<IReadOnlyDictionary<ulong, float[]>>` — collection-agnostic, so it addresses the chunks collection unchanged | `IVectorRoles.cs:17-20` |
| P6 | Signature | `ObjectSearchGrpcService` is a primary-constructor class; injecting `IResultDiversifier diversifier` is the whole wiring mechanism, and `Iverson.Api` already references `Iverson.Vector` | `ObjectSearchGrpcService.cs:30-38` (`IResultReranker reranker)` is the last parameter); `using Iverson.Vector;` at `:9` |
| P7 | Signature | `FetchCentroidsAsync` is the existing precedent for a scoped, degrade-on-failure retrieve — mint the key inside `RequestHeaders.Use`, `try`/`catch (Exception ex) when (ex is not OperationCanceledException)`, log a warning, return an empty map | `ObjectSearchGrpcService.cs:650-669`; `EmptyCentroids` at `:642` |
| P8 | Command | `dotnet build Iverson.Server/Iverson.Server.slnx`, `dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj`, `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` are the valid invocations | `Iverson.Server/Iverson.Server.slnx` exists (no `.sln`); both csproj paths confirmed in P2; identical commands used by `docs/plans/2026-07-29-tensor-reranking-implementation-plan.md:160-161` |
| P9 | Code validity | `System.Numerics.Tensors` 10.0.10 is already referenced by `Iverson.Vector`; `InternalsVisibleTo` names only `Iverson.Vector.Tests`, so the new types must be `public` for `Iverson.Api` to consume them | `Iverson.Vector.csproj` — `PackageReference Include="System.Numerics.Tensors" Version="10.0.10"`; the single `InternalsVisibleTo` `_Parameter1` is `Iverson.Vector.Tests` |
| P10 | Code validity | `Iverson.Vector.Tests` has xUnit 2.9.3, FluentAssertions 7.0.0, NSubstitute 5.3.0 — the conventions `ResultRerankerTests.cs` already uses | `Iverson.Vector.Tests.csproj` package references |
| P11 | Ordering | Task 1 must precede Tasks 2–3 (they will not compile without `IResultDiversifier`). Task 2 adds the constructor parameter and updates the sole construction site; Task 3 depends on that parameter already existing. The order is genuinely sequential, not arbitrary | Compile dependency; construction site per P12 |
| P12 | Consumer impact | `ObjectSearchGrpcService` has exactly **one** construction site outside DI, and it passes arguments **positionally** — adding a constructor parameter requires appending `new ResultDiversifier()` there and nowhere else | `grep -rn "new ObjectSearchGrpcService"` returns only `ObjectSearchGrpcServiceTests.cs:65`, positional through `new ResultReranker()` at `:69` |
| P13 | Consumer impact *(sibling sweep — all 8 `RetrieveNamedVectorAsync` sites in the Api test file)* | Both `DidNotReceive()` assertions (`:2382`, `:2533`) belong to **`SearchSimilar`** tests, which gain no retrieve, so they are unaffected. Of the four `Arg.Any<string>()`-collection stubs, `:2346` and `:2553` are `SearchSimilar`; `:2499` throws for every retrieve (both of `SearchChunks`' retrieves throw → fused order, still passes); `:2462` is the one the spec mandates updating. `:2604` (`SearchChunks_WhenRerankPermutesOrder_...`) is a fifth case the spec's A9 assessed only for ranking: its stub will also serve the chunk-vector retrieve, but its map is keyed by `KeyToUlong(parentGuid)` while the chunk ids are `1` and `2`, so every chunk lookup misses, diversity vectors are absent, MMR degrades to fused order and its assertions still hold | `grep -n "RetrieveNamedVectorAsync"` over `ObjectSearchGrpcServiceTests.cs` (8 hits: `:2346`, `:2382`, `:2462`, `:2476`, `:2499`, `:2533`, `:2553`, `:2604`), each read against its enclosing `[Fact]` at `:2333`, `:2362`, `:2445`, `:2485`, `:2515`, `:2540`, `:2575` |
| P14 | Consumer impact | `ServiceCollectionExtensionsTests` asserts only that specific services resolve; it makes no registration-count or exhaustive-set assertion, so a new `AddSingleton` breaks nothing | `Iverson.Vector.Tests/ServiceCollectionExtensionsTests.cs` — three facts, each resolving `QdrantClient` and asserting `NotBeNull`/`BeOfType` |
| P15 | Consumer impact | `.Rerank(...).Take(...)` appears at exactly two call sites, both in scope; no other consumer of `IResultReranker` exists in production code | `grep -rn "IResultReranker\|\.Rerank("` over non-test, non-`obj` sources: `ObjectSearchGrpcService.cs:38,:254,:395`, plus the type's own declaration and DI registration |

## Tasks

### Task 1: The diversifier

**Files:**
- Create: `Iverson.Server/Iverson.Vector/IResultDiversifier.cs`
- Create: `Iverson.Server/Iverson.Vector/ResultDiversifier.cs`
- Create: `Iverson.Server/Iverson.Vector.Tests/ResultDiversifierTests.cs`
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs:50`

**Interfaces:**
- Produces: `IResultDiversifier`, `DiversifyCandidate`, and the DI registration — Tasks 2 and 3 both consume these.

- [ ] **Step 1: Write the contract.**

`Iverson.Server/Iverson.Vector/IResultDiversifier.cs`:

```csharp
namespace Iverson.Vector;

/// <summary>
/// A candidate for diversified selection: its id, its fused score, and the vector whose
/// mutual cosine similarity defines redundancy. The vector is SUPPLIED, not derived — each
/// RPC decides what "diversity" means at the granularity of what it returns.
/// </summary>
public sealed record DiversifyCandidate(ulong Id, double Score, float[]? DiversityVector);

public interface IResultDiversifier
{
    /// <param name="ranked">Candidates in fused-descending order, as <c>IResultReranker.Rerank</c> returns them.</param>
    IReadOnlyList<RerankedResult> Diversify(IReadOnlyList<DiversifyCandidate> ranked, int topK);
}
```

- [ ] **Step 2: Write the tests** (they will not compile until Step 3 adds the implementation type; write them first and let the red state be the compile failure).

`Iverson.Server/Iverson.Vector.Tests/ResultDiversifierTests.cs` covers exactly the spec's §7 formula cases:

- λ arithmetic on a hand-computed fixture with all vectors present;
- a lower-fused but dissimilar candidate is promoted over a higher-fused near-duplicate;
- the highest-fused candidate is always selected first, including when it is the most redundant;
- **every diversity vector absent → the selected set AND its order are identical to `ranked.Take(topK)`, asserted exactly** (compare the full `RerankedResult` sequence, not just ids);
- one vector absent → that pair contributes no penalty;
- differing lengths → treated as absent and, specifically, **does not throw**;
- a zero-magnitude vector → `NaN` treated as absent, and a `NaN`-first candidate is not selected ahead of a strictly better one;
- pool smaller than `topK` → every candidate returned, MMR determines their order;
- empty pool → empty result;
- `topK` of 1 → the highest-fused candidate.

Mirror `ResultRerankerTests.cs` conventions (xUnit `[Fact]`, FluentAssertions).

- [ ] **Step 3: Implement.**

`Iverson.Server/Iverson.Vector/ResultDiversifier.cs`:

```csharp
using System.Numerics.Tensors;

namespace Iverson.Vector;

/// <summary>
/// Greedy maximal marginal relevance over an already-fused, fused-descending candidate list.
/// Pure and I/O-free. Selection replaces a plain Take(topK): the first candidate is always the
/// highest-fused one, and each subsequent pick maximises lambda*fused - (1-lambda)*maxSim.
/// </summary>
public sealed class ResultDiversifier : IResultDiversifier
{
    private const double Lambda = 0.70;

    public IReadOnlyList<RerankedResult> Diversify(IReadOnlyList<DiversifyCandidate> ranked, int topK)
    {
        if (ranked.Count == 0 || topK <= 0) return [];

        var take      = Math.Min(topK, ranked.Count);
        var selected  = new List<RerankedResult>(take);
        var taken     = new bool[ranked.Count];

        // Running maximum similarity of each remaining candidate against the SELECTED set,
        // updated against only the newly-selected candidate each round. NaN is never stored:
        // an unusable similarity is an ABSENT one, leaving the running maximum untouched.
        var maxSim = new double[ranked.Count];

        // Step 1 of the mechanism: the highest-fused candidate is selected unconditionally.
        // `ranked` is fused-descending, so that is index 0.
        Select(0);

        while (selected.Count < take)
        {
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;

            for (var i = 0; i < ranked.Count; i++)
            {
                if (taken[i]) continue;

                var mmr = Lambda * ranked[i].Score - (1 - Lambda) * maxSim[i];

                // Strict `>` keeps the EARLIER candidate on a tie, and `ranked` is
                // fused-descending — the exactness of the all-absent guarantee rests on this.
                if (mmr > bestScore)
                {
                    bestScore = mmr;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) break;
            Select(bestIndex);
        }

        return selected;

        void Select(int index)
        {
            taken[index] = true;
            selected.Add(new RerankedResult(ranked[index].Id, ranked[index].Score));

            var justSelected = ranked[index].DiversityVector;
            if (justSelected is null) return;

            for (var i = 0; i < ranked.Count; i++)
            {
                if (taken[i]) continue;

                var other = ranked[i].DiversityVector;

                // Length equality is checked BEFORE the call: CosineSimilarity THROWS
                // ArgumentException on differing lengths (and on two empty spans), it does
                // not return NaN. A mismatched pair has no similarity term.
                if (other is null || other.Length != justSelected.Length || other.Length == 0) continue;

                double similarity = TensorPrimitives.CosineSimilarity(justSelected, other);

                // A zero-magnitude vector yields NaN. Treat it as absent rather than letting it
                // reach the argmax, where every `>` comparison against it is false.
                if (double.IsNaN(similarity)) continue;

                if (similarity > maxSim[i]) maxSim[i] = similarity;
            }
        }
    }
}
```

- [ ] **Step 4: Register beside the re-ranker.**

In `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs`, after line 50:

```csharp
        services.AddSingleton<IResultDiversifier, ResultDiversifier>();
```

- [ ] **Step 5: Build and test.**

```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
```

- [ ] **Step 6: Commit.**

```bash
git add Iverson.Server/Iverson.Vector/IResultDiversifier.cs Iverson.Server/Iverson.Vector/ResultDiversifier.cs Iverson.Server/Iverson.Vector.Tests/ResultDiversifierTests.cs Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs
git commit -m "feat(search): greedy MMR result diversifier"
```

---

### Task 2: Diversify `SearchSimilar`

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:30-38` (constructor), `:246-254`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs:65-70` (construction site) and new facts

**Interfaces:**
- Consumes: `IResultDiversifier` and `DiversifyCandidate` from Task 1.
- Produces: the `diversifier` constructor parameter, which Task 3 uses without re-adding.

- [ ] **Step 1: Inject the diversifier.**

Append a parameter to the primary constructor, after `IResultReranker reranker`:

```csharp
    IResultReranker reranker,
    IResultDiversifier diversifier)
```

Update the sole non-DI construction site — `ObjectSearchGrpcServiceTests.cs:65-70` passes arguments positionally, so append `new ResultDiversifier()` after `new ResultReranker()`.

- [ ] **Step 2: Replace the trim.**

`SearchSimilar` already holds each candidate's own `<property>_centroid` in `centroids` (`:234-242`), which is the correct diversity vector for object entries — the granularity already matches and **no additional retrieve is issued**. Replace the `Take` at `:254`:

```csharp
        var diversityCandidates = reranker.Rerank(queryVector, candidates)
            .Select(r => new DiversifyCandidate(
                r.Id,
                r.FusedScore,
                centroids.TryGetValue(r.Id, out var v) ? v : null))
            .ToList();

        foreach (var ranked in diversifier.Diversify(diversityCandidates, (int)topK))
```

The body of the loop is unchanged: `ranked.FusedScore` is still the fused score written to `SearchResponse.Score`, and the `byId` re-join is untouched. Do **not** touch the over-fetch gate at `:213`.

- [ ] **Step 3: Add service-level tests** in `ObjectSearchGrpcServiceTests.cs`:

- `SearchSimilar` on a dual-annotated property — two near-identical centroids and one dissimilar one, with the dissimilar candidate promoted over the near-duplicate despite a lower fused score;
- `SearchSimilar` on an embedding-only property — results are unchanged from the fused order (no centroids, so every diversity vector is absent);
- `SearchSimilar` issues **no** additional retrieve beyond part 3's existing centroid fetch (`Received(1)` on `RetrieveNamedVectorAsync`).

- [ ] **Step 4: Build and test.**

```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 5: Commit.**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "feat(search): diversify SearchSimilar results with MMR"
```

---

### Task 3: Diversify `SearchChunks`, and document the change

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:366-395`, and the private-helpers region near `:640-669`
- Modify: `Iverson.Clients/Common/Proto/object_search.proto:86-90`, `:126-129`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` (new facts + one existing-test update at `:2445`)

**Interfaces:**
- Consumes: `IResultDiversifier`/`DiversifyCandidate` from Task 1 and the `diversifier` constructor parameter from Task 2.

- [ ] **Step 1: Fetch the chunk vectors.**

The parent centroid `SearchChunks` already holds is the **wrong granularity** — its granularity is the document while the entries are passages. Add a second retrieve, against the **chunks** collection under the same `vectorName` the search itself just used, for the candidates' own ids. Place it after the parent-centroid fetch at `:373-379`, following `FetchCentroidsAsync`'s scoped, degrade-on-failure shape (`:650-669`):

```csharp
        // The DIVERSITY vector for a chunk is the chunk's OWN vector — the same representation
        // Qdrant matched the query against — not its parent centroid, which is the re-rank
        // signal above and lives at document granularity. Distinct collections, distinct
        // signals; neither substitutes for the other. A failure here degrades selection to
        // the fused order rather than failing the search.
        var chunkVectors = EmptyCentroids;
        if (results.Count > 0)
        {
            try
            {
                using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(chunksCollection, readOnly: true)))
                    chunkVectors = await vector.RetrieveNamedVectorAsync(
                        chunksCollection, results.Select(r => r.Id).ToList(), vectorName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "[SearchChunks] chunk-vector retrieve failed (collection={Collection} vector={Vector} ids={Count}); " +
                    "selecting without the diversity signal.",
                    chunksCollection.SanitizeForLog(), vectorName.SanitizeForLog(), results.Count);
                chunkVectors = EmptyCentroids;
            }
        }
```

- [ ] **Step 2: Replace the trim.**

Replace the `Take` at `:395`. The diversity vector comes from `chunkVectors`, keyed by the chunk's own id — **not** from `RerankCandidate.Centroid`, which remains the parent centroid serving part 3's re-rank signal unchanged:

```csharp
        var diversityCandidates = reranker.Rerank(queryVector, candidates)
            .Select(r => new DiversifyCandidate(
                r.Id,
                r.FusedScore,
                chunkVectors.TryGetValue(r.Id, out var v) ? v : null))
            .ToList();

        foreach (var ranked in diversifier.Diversify(diversityCandidates, (int)topK))
```

The loop body, the `byId` re-join and the fused `Score` are unchanged. Do **not** change the over-fetch at `:345`.

- [ ] **Step 3: Update the existing batching test.**

`SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds` (`:2445`) stubs `RetrieveNamedVectorAsync` with `Arg.Any<string>()` for the collection, so it now intercepts **both** retrieves and its captured locals are overwritten by the second — `Received(1)`, `capturedIds.ContainSingle()`, `capturedCollection` and `capturedVectorName` all fail. Discriminate the two stubs by collection (`Arg.Is<string>(c => c == "articles_test-tenant")` for the parent-centroid retrieve, and a separate stub for the chunks collection), so the test continues to assert the parent-centroid retrieve's batching to distinct parent ids.

Two neighbouring tests need **no** change and must not be "fixed": `SearchChunks_CentroidRetrieveThrows_KeepsRawCosineOrder` (`:2485`) throws for every retrieve, so both degrade and the fused order stands; `SearchChunks_WhenRerankPermutesOrder_...` (`:2575`) returns a map keyed by `KeyToUlong(parentGuid)` while the chunk ids are `1` and `2`, so every chunk-vector lookup misses, the diversity vectors are absent, and its assertions hold unchanged.

- [ ] **Step 4: Add service-level tests:**

- the diversity vectors come from a retrieve against the **chunks** collection under `<property>_vector`, distinct from part 3's parent-centroid retrieve against the object collection — assert **both** retrieves occur, addressing different collections;
- two near-identical chunks are suppressed relative to a dissimilar one **even when all three share a parent**, and two dissimilar chunks sharing a parent are **not** suppressed — the pair of cases that distinguishes chunk-level from parent-level diversity;
- the chunk-vector retrieve throwing leaves the results in fused order rather than failing the call, and exactly `topK` results are streamed.

- [ ] **Step 5: Document the ordering change in the proto.**

MMR changes the **order** of returned results, not merely which are returned: the top three will no longer necessarily be the three highest-fused candidates. Extend the existing `score` comments — `object_search.proto:86-90` (`SearchResponse`) and `:126-129` (`ChunkSearchResponse`) — to record this alongside part 3's note. Comments only; no client regeneration (P3).

- [ ] **Step 6: Build and test.**

```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 7: Commit.**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs Iverson.Clients/Common/Proto/object_search.proto
git commit -m "feat(search): diversify SearchChunks results on chunk-level vectors"
```

## Tasks NOT in this plan

Inherited from the spec's "Not in this spec". A new spec → new plan cycle is required to add any of these.

- **Cross-corpus cluster centroids.** Deferred by 4a and still deferred.
- **Topic discovery / corpus browsing.** Would need the cluster artifact this spec does not build.
- **A per-parent hard cap.** With chunk-level diversity vectors, same-parent chunks are no longer suppressed merely for sharing a parent — only for actually resembling one another, which is the intended behavior. If same-document crowding turns out to need a hard limit independent of passage similarity, that is a separate decision on measured evidence.
- **Caller-supplied λ or a per-request opt-out.** Fixed server-side constants, per part 3.
- **The DSL `Search` path and `VECTOR_SIMILAR` clauses.** Scope is `SearchChunks` and `SearchSimilar`, matching part 3.

## Known issues inherited from spec

These exist in the implementation by design — accepted during brainstorming.

**Diversification cost grows with `top_k × pool size`.** The incremental form costs `topK × 4 × topK` similarity computations — quadratic in `top_k`. At the small `top_k` values RAG consumers actually use this is negligible (`top_k = 10` is 400 comparisons), but part 3 deliberately left `top_k` unclamped, so `top_k = 1000` would mean roughly four million 768-dimension cosine computations on the request thread. Accepted on the same reasoning part 3 accepted its uncapped `4N` fetch: the cost is proportional to what the caller explicitly asked for, and clamping it would be a silent behaviour change. If it ever bites, the fix is a threshold above which diversification is skipped — not a cap on `top_k`.

**`SearchChunks` gains a second Qdrant round trip per search.** The chunk-vector retrieve is an additional call over `4 × top_k` ids, on top of part 3's parent-centroid retrieve. It is what buys passage-level diversity rather than document-level, and it is paged by the same batching `RetrieveNamedVectorAsync` already applies. `SearchSimilar` is unaffected and still issues no retrieve beyond part 3's.

**λ is uncalibrated.** 0.70 is a reasonable default from the MMR literature, not a value tuned against this corpus, and the motivating problem has not been measured. λ is a compile-time constant precisely so it can be revised centrally once there is evidence to revise it against.

**4a's `ComputeCentroid` has no zero-magnitude guard** (`IntelligenceStoreConsumer.cs:375-381`). A zero-magnitude chunk vector yields a `NaN` centroid, which already sinks that document to the bottom of every part 3 result set silently. Ben was shown this finding and elected not to expand scope; the diversifier's own `NaN` guard means 4b is unaffected either way.
