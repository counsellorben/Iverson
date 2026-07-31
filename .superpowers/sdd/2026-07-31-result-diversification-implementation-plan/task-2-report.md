# Task 2 Report: Diversify `SearchSimilar`

## Changes

- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
  - Primary constructor: appended `IResultDiversifier diversifier` after `IResultReranker reranker`.
  - `SearchSimilar`: replaced `reranker.Rerank(queryVector, candidates).Take((int)topK)` with a two-step
    pipeline — map the reranker's fused-descending output into `DiversifyCandidate` records (id, fused
    score, and the same object-centroid used for reranking, or `null` when absent), then call
    `diversifier.Diversify(diversityCandidates, (int)topK)`. The loop body (byId re-join, `Score =
    (float)ranked.FusedScore`, field masking) is untouched. No additional Qdrant call is introduced —
    `centroids` is the same dictionary already fetched once at `:234-242` for reranking; it is reused,
    not re-fetched.
  - Did **not** touch the over-fetch gate at `:213` or `SearchChunks`/`Search`/DSL paths.

- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`
  - Updated the sole non-DI construction site (`_sut = new ObjectSearchGrpcService(...)`) to append
    `new ResultDiversifier()` after `new ResultReranker()`.
  - Added 3 new facts under a new "Result diversification (MMR)" section:
    - `SearchSimilar_PromotesDissimilarCandidate_OverNearDuplicate_DespiteLowerFusedScore` — 3
      dual-annotated candidates (A, B, C). A and B share centroid `e0` (cosine ≈ 1.0, near-duplicate);
      C's centroid is orthogonal (`e1`). Base scores/centroids were chosen so hand-computed fused
      scores are A=1.0000, B=0.9000, C=0.6667 (fused-descending: A, B, C). Hand-computed MMR (λ=0.70)
      after A is selected: Mmr(B) = 0.7×0.9000 − 0.3×1.0 = 0.33; Mmr(C) = 0.7×0.6667 − 0.3×0.0 =
      0.4667. C wins and is selected second at topK=2, so the returned order is [A, C] — B (fused
      2nd-highest but redundant with A) is excluded. Asserted via the `body` payload field and `Score`.
    - `SearchSimilar_EmbeddingOnlyProperty_ResultsUnchangedFromFusedOrder` — embedding-only property
      (Article/Title, no centroid ever fetched), asserting the returned scores are bit-for-bit the
      fused-descending order (identical to the pre-existing `SearchSimilar_NoCentroidAndNoDecayField_...`
      test's assertion, confirming the diversifier is a no-op when every `DiversityVector` is null).
    - `SearchSimilar_Diversification_IssuesNoAdditionalRetrieve` — dual-annotated schema, asserts
      `RetrieveNamedVectorAsync` is `Received(1)` (the existing part-3 centroid fetch), i.e.
      diversification adds no I/O.

- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs`
  - **Deviation from brief**: the brief said "there is exactly one construction site outside DI" at
    `ObjectSearchGrpcServiceTests.cs:65`. There is in fact a second one — `BuildSut()` in this
    integration-test file (line 74-84), which also builds `ObjectSearchGrpcService` positionally and
    ended with `new ResultReranker())`. The build failed with CS7036 until this was updated too.
    Appended `new ResultDiversifier())` after `new ResultReranker(),`. `Iverson.Vector` was already
    `using`'d in this file, so no new import was needed. This file's tests use a live Qdrant
    testcontainer and aren't run as part of the standard `dotnet test` filter used here (they're
    gated behind a container fixture); the fix was verified by `dotnet build` succeeding.

## Test commands run

```
dotnet build Iverson.Server/Iverson.Server.slnx
```
→ Build succeeded, 0 warnings (new), 0 errors.

```
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```
→ `Passed! - Failed: 0, Passed: 571, Skipped: 0, Total: 571, Duration: 2 m 2 s`

## Notes for reviewer

- The brief's "exactly one construction site outside DI" claim was incomplete — see deviation note
  above. Worth flagging back to whoever verified the plan's assumptions, since Task 3 will hit the
  same construction site again if it also isn't checked.
- The MMR promotion test's fixture is deliberately over-documented with the hand-computed arithmetic
  in a code comment, per the ambiguity resolution given — a reviewer can re-derive the numbers from
  the comment without re-running the code.
- `System.Numerics.Tensors.TensorPrimitives.CosineSimilarity` is used identically by both
  `ResultReranker` (query-vs-centroid) and `ResultDiversifier` (centroid-vs-centroid); the test fixture
  exploits that the same centroid vector feeds both computations, which is why the fused-score and
  MMR arithmetic interact the way the comment describes.
