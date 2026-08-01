# Diversification Deferred Rulings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-31-diversification-deferred-rulings-design.md` (commit SHA: `91d515a`)

**Goal:** Remove the duplicated retrieve shape part 4b introduced, stop issuing a Qdrant round trip that provably cannot affect the answer, make a degenerate chunk embedding fail visibly instead of silently poisoning a document's centroid, and record the one bias the design accepts by construction.

**Architecture:** Two independent changes. In `ObjectSearchGrpcService`, `FetchCentroidsAsync` generalizes into a signal-neutral `RetrieveVectorsOrDegradeAsync` serving all three retrieve sites, and the chunk-vector site gains a gate derived from when MMR can actually read a diversity vector. In `IntelligenceStoreConsumer`, the caller filters zero-magnitude vectors out of the centroid input only, leaving the chunk-point writes untouched.

**Tech stack:** .NET 10, Qdrant.Client 1.18.1, xUnit 2.9.3 + NSubstitute 5.3.0 + FluentAssertions 7.0.0.

---

## Global Constraints

Copied from the spec; both tasks must hold to these.

- **An absent signal is never replaced by a substituted value.** This is the rule the whole initiative rests on — it is why §3 omits a centroid rather than writing a degenerate one, and why §4 documents the absent-vector bias rather than curing it.
- **Failures degrade, never throw.** A failed retrieve logs and returns an empty map; cancellation still propagates (`catch … when (ex is not OperationCanceledException)`).
- **Part 4b's diversifier is not touched.** `λ`, the incremental `maxSim`, the presence/magnitude split and the seeded argmax stay exactly as part 4b left them.
- **Part 3's over-fetch gate is not touched** and gains no new condition.
- **The chunk-point write loop is not touched.** Every chunk keeps its own `<property>_vector`, degenerate ones included.
- **Scope is `SearchChunks` and `SearchSimilar` only** — not the DSL `Search` path, not `VECTOR_SIMILAR` clauses.

## File Structure

**Modify**
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — generalize the retrieve helper, convert three call sites, gate the chunk-vector retrieve, document the absent-vector bias.
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — filter degenerate vectors out of the centroid input; correct the stale comment on `ComputeCentroid`.

**Test**
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — the `top_k = 1` gate test.
- `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs` — degenerate-vector cases, plus repair of four zero-vector fixtures (P22).

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time (A1–A22) and **not** re-verified here. The load-bearing ones:

- **A1–A2:** `FetchCentroidsAsync` has exactly two call sites plus its declaration; `EmptyCentroids` is referenced only within `ObjectSearchGrpcService.cs`.
- **A3:** `SanitizeForLog` is `value.ReplaceLineEndings("")`; compile-time constants passed as structured arguments need none.
- **A7–A8:** at `take == 1` the diversifier's selection loop never runs, so no diversity vector is read and the output is `[ranked[0]]` either way. `take == 1` iff `topK == 1` or the pool is 1.
- **A9:** no existing test uses `TopK = 1`.
- **A11–A13:** the `centroids` dictionary tolerates an omitted key; an empty dictionary skips the write entirely; and part 3 handles a wholly absent centroid, whereas a `NaN` one it does not.
- **A16:** `ComputeCentroid` is `static` while `logger` is an instance member, and the type/key context lives at the caller — which is why the guard is caller-side.
- **A21:** `ComputeCentroid` throws `IndexOutOfRangeException` on an empty input list, so the caller must check before calling.
- **A22:** the pool `Diversify` sees has the same count as `results`.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time against `main@91d515a`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | All four touched files exist at the cited paths | `ls` of both production files and both test files returned all four |
| P2 | Signature | `chunkResults` is an array, so `.Length` is valid | `IntelligenceStoreConsumer.cs:207` — `var chunkResults = await Task.WhenAll(chunkTasks);` |
| P3 | Signature | The tuple element is named `chunkVector` | `:209` — `chunkResults.Select(r => r.chunkVector)` |
| P4 | Signature | `ev.TypeName`, `ev.Key`, `cf.PropertyName` and `logger` are all in scope at `:209` | `cf.PropertyName` used at `:186` and `:210`; `ev.TypeName`/`ev.Key` used in the log at `:245`; `logger` is a primary-constructor member at `:38` |
| P5 | Code validity | `SanitizeForLog` is already used in this file, so the new warning matches an established convention rather than importing one | `IntelligenceStoreConsumer.cs:443`, `:461`, `:494`. Note `:443` sanitizes `TypeName` but not `Key`; this plan sanitizes both plus the field name, which is strictly safer and uses the same helper |
| P6 | Signature | The three call sites' local names are as the plan's code blocks use them | `SearchSimilar`: `collectionName`, `results`, `vectorDesc` (`:236-242`). `SearchChunks` parent: `parentIds`, `schema.CollectionName`, `decision.TenantValue`, `chunkDesc` (`:373-388`). `SearchChunks` chunk-vector: `chunksCollection`, `vectorName`, `results`, `topK` (`:337-347`, `:400`) |
| P7 | Signature | The helper body needs no signature changes — `RequestHeaders.Use`, `MintScopedApiKey` and `RetrieveNamedVectorAsync` are carried over verbatim from the existing method | The new body is the existing `FetchCentroidsAsync` body (`:690-709`) with the log's trailing clause parameterized |
| P8 | Command | `dotnet build Iverson.Server/Iverson.Server.slnx` and `dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj` are valid | Both paths confirmed present; identical commands used by the part-4b plan and run green on this tree |
| P9 | Command | `refactor(search):` does not introduce a new commit-type convention | `git log --oneline -200` type histogram: `feat` 57, `fix` 43, `docs` 18, `test` 6, `chore` 6, `style` 1, **`refactor` 1**. Both types this plan uses already exist |
| P10 | Ordering | Tasks 1 and 2 are genuinely independent and may run in either order | Different files, no shared symbol introduced by either. `ObjectSearchGrpcService.cs` does reference `IntelligenceStoreConsumer` (5 hits, all `KeyToUlong`), but Task 2 changes neither `KeyToUlong` nor `ComputeCentroid`'s signature |
| P11 | Code validity | `magnitude == 0` on an accumulated `float` compiles with no analyzer treating float equality as an error | Neither `Iverson.Api.csproj` nor `Iverson.Api.Tests.csproj` sets `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild` or `AnalysisMode`; no `Directory.Build.props` exists |
| P12 | Consumer impact | The three symbols this plan touches have had their consumers swept by name — none is a constructor, so name-grep is a complete strategy here | `FetchCentroidsAsync` → `:238`, `:382`, `:690`. `EmptyCentroids` → 7 hits, all in-file. `ComputeCentroid` → one production call, one declaration, two tests. (The part-4b miss was a target-typed `new(` hiding from a `new TypeName(` pattern; no symbol here is a constructor) |
| P13 | Consumer impact **(shape-changing — see Task 2 Step 1)** | The consumer tests' embedding stub returns a **zero vector**, so Task 2's filter would drop every chunk vector and write no centroid, failing five existing assertions | `IntelligenceStoreConsumerTests.cs:64` — `.Returns(new float[768])`; further zero-vector fixtures at `:103`, `:578`, `:1166`. Five tests assert `body_centroid` presence: `:1449`, `:1491`, `:1551`, `:1617`, `:1680`. **Consequence worth recording:** today those tests store an all-`NaN` centroid (`0/0` per component) and pass anyway, because they assert only key presence and never the values — the suite has been exercising the exact failure §3 exists to prevent |
| P14 | Consumer impact | Repairing those fixtures is safe — no test asserts vector contents | All four `new float[768]` occurrences are stub return values (`:64`, `:103`, `:578`, `:1166`); none is compared component-wise anywhere in the file |
| P15 | Consumer impact *(sibling sweep — every existing test that depends on the chunk-vector retrieve firing)* | All satisfy the new gate `results.Count > 1 && topK > 1`, so §2 breaks none of them | `SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds` (`:2559`): 3 results, `TopK = 5`. `SearchChunks_SuppressesNearDuplicatePassage_…` (`:2804`): 3 results, `TopK = 2`. `SearchChunks_ChunkVectorRetrieveThrows_…` (`:2856`): 5 results, `TopK = 3` |

## Tasks

### Task 1: Unify the retrieves, gate the chunk-vector fetch, document the bias

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

- [ ] **Step 1: Generalize the helper.**

Replace `FetchCentroidsAsync` together with its doc comment (`:685-709`; the summary opens at `:686`, the declaration is at `:690`) with:

```csharp
    /// <summary>
    /// Retrieves a named vector for a set of point ids under its own scoped api-key. A failure here
    /// degrades the ranking rather than failing the search: every vector becomes ABSENT (never a
    /// substituted neutral value). The caller names the consequence so the log stays specific.
    /// </summary>
    private async Task<IReadOnlyDictionary<ulong, float[]>> RetrieveVectorsOrDegradeAsync(
        string collection, IReadOnlyList<ulong> ids, string vectorName, string rpcName, string consequence)
    {
        if (ids.Count == 0) return EmptyVectors;

        try
        {
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collection, readOnly: true)))
                return await vector.RetrieveNamedVectorAsync(collection, ids, vectorName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "[{Rpc}] retrieve failed (collection={Collection} vector={Vector} ids={Count}); {Consequence}.",
                rpcName, collection.SanitizeForLog(), vectorName.SanitizeForLog(), ids.Count, consequence);
            return EmptyVectors;
        }
    }
```

Rename the `EmptyCentroids` field (`:682`) to `EmptyVectors` and update its remaining uses. `rpcName` and `consequence` are compile-time constants at every call site and need no sanitizing (A3).

- [ ] **Step 2: Convert the `SearchSimilar` call site** (`:236-242`).

```csharp
            centroids = await RetrieveVectorsOrDegradeAsync(
                collectionName,
                results.Select(r => r.Id).ToList(),
                vectorDesc.PropertyName.ToSnakeCase() + "_centroid",
                "SearchSimilar",
                "re-ranking without the centroid signal");
```

- [ ] **Step 3: Convert the `SearchChunks` parent-centroid call site** (`:381-387`).

The `parentIds.Count > 0 ? … : EmptyCentroids` ternary collapses, because the helper's own `ids.Count == 0` guard covers it:

```csharp
        var centroids = await RetrieveVectorsOrDegradeAsync(
            tenantScope.ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks: false),
            parentIds,
            chunkDesc.PropertyName.ToSnakeCase() + "_centroid",
            "SearchChunks",
            "re-ranking without the centroid signal");
```

- [ ] **Step 4: Convert and gate the chunk-vector call site** (`:392-412`).

```csharp
        // The DIVERSITY vector for a chunk is the chunk's OWN vector — the same representation
        // Qdrant matched the query against — not its parent centroid, which is the re-rank signal
        // above and lives at document granularity. Distinct collections, distinct signals.
        //
        // Skipped when diversification provably cannot act: MMR reads a diversity vector only
        // inside the selection loop, which runs only when Math.Min(topK, pool) >= 2. Below that the
        // retrieve cannot change the returned set OR its order. Deliberately NOT gated on
        // pool > topK — MMR reorders even when the pool is exactly topK.
        var chunkVectors = EmptyVectors;
        if (results.Count > 1 && topK > 1)
        {
            chunkVectors = await RetrieveVectorsOrDegradeAsync(
                chunksCollection,
                results.Select(r => r.Id).ToList(),
                vectorName,
                "SearchChunks",
                "selecting without the diversity signal");
        }
```

- [ ] **Step 5: Document the absent-vector bias** at both pairing sites (`:255-260` and `:428-434`), immediately above each `var diversityCandidates = …`:

```csharp
        // A candidate whose diversity vector is ABSENT contributes no similarity term and so takes
        // no penalty, which means it outranks an otherwise-equal candidate that has a vector and
        // any positive similarity. Accepted by design: substituting a value would break the
        // bit-exact Take(topK) degradation guarantee. See the design spec's Known issues.
```

- [ ] **Step 6: Add the gate test** to `ObjectSearchGrpcServiceTests.cs`: a `SearchChunks` call with `TopK = 1` issues **no** retrieve against the chunks collection, while the parent-centroid retrieve against the object collection still fires. Discriminate the two by collection (`articles_test-tenant` vs `articles_chunks_test-tenant`), as the existing batching test does.

- [ ] **Step 7: Build and test.**

```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 8: Commit.**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "refactor(search): unify vector retrieves and skip the no-op diversity fetch"
```

---

### Task 2: Filter degenerate vectors out of the centroid input

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

- [ ] **Step 1: Repair the zero-vector test fixtures first** (P13/P14), so the rest of this task's work is measured against a suite that exercises real vectors.

`IntelligenceStoreConsumerTests.cs` returns an all-zero `new float[768]` from its embedding stubs at `:64`, `:103`, `:578` and `:1166`. Replace each with a non-degenerate unit vector (a `float[768]` with one component set to `1f`). No test compares vector contents (P14), so nothing else changes.

Run the suite before moving on and confirm it is green — this establishes that the five `body_centroid` assertions (`:1449`, `:1491`, `:1551`, `:1617`, `:1680`) pass on real vectors, not on the all-`NaN` centroid they have been silently accepting.

- [ ] **Step 2: Filter the centroid input** at `:209-210`.

```csharp
                    // Filter degenerate vectors from the CENTROID INPUT ONLY. A zero-magnitude
                    // vector makes ComputeCentroid divide by zero and store a NaN centroid, which
                    // part 3 fuses into a NaN score that sinks the document to the bottom of every
                    // result set — silently. The chunk-point write loop below is UNCHANGED: every
                    // chunk keeps its own vector, degenerate ones included, because part 4b's
                    // diversifier already treats a NaN cosine as an absent signal.
                    var centroidInput = chunkResults
                        .Select(r => r.chunkVector)
                        .Where(v => !IsZeroMagnitude(v))
                        .ToList();

                    var degenerate = chunkResults.Length - centroidInput.Count;
                    if (degenerate > 0)
                        logger.LogWarning(
                            "[Intelligence] Dropped {Count} zero-magnitude chunk vector(s) from the centroid for {Type}:{Key} field={Field}",
                            degenerate,
                            ev.TypeName.SanitizeForLog(),
                            ev.Key.SanitizeForLog(),
                            cf.PropertyName.SanitizeForLog());

                    // No centroid at all when nothing survives — an ABSENT centroid is a state part 3
                    // handles; a NaN one is not. ComputeCentroid would also throw on an empty list.
                    if (centroidInput.Count > 0)
                        centroids[$"{cf.PropertyName.ToSnakeCase()}_centroid"] = ComputeCentroid(centroidInput);
```

- [ ] **Step 3: Add the magnitude predicate** beside `ComputeCentroid`:

```csharp
    // Mirrors ComputeCentroid's own magnitude computation. A vector whose components are small
    // enough to underflow when squared also lands here, since the accumulated magnitude is then
    // exactly zero — which is the case that would divide to Infinity rather than NaN.
    private static bool IsZeroMagnitude(float[] vector)
    {
        float magnitude = 0;
        foreach (var component in vector)
            magnitude += component * component;
        return magnitude == 0;
    }
```

- [ ] **Step 4: Correct the stale comment** at `:361-362`. It currently reads:

> `// No zero-magnitude guard: no caller passes a zero vector (blank text is skipped upstream,`
> `// and SplitIntoChunks always yields at least one chunk from non-blank text).`

That reasoning is about the input **text**; the failure mode is the embedding **model** returning a zero vector for non-blank text, which it never covered. Replace it with a note that the caller now filters zero-magnitude vectors before calling, so the invariant is enforced at the boundary rather than assumed.

- [ ] **Step 5: Add the degenerate-vector tests** to `IntelligenceStoreConsumerTests.cs`:

- one degenerate chunk vector among several is dropped from the centroid, and a centroid is still written from the survivors;
- when **every** chunk vector is degenerate, no `<field>_centroid` entry is written — **and the chunk points are still upserted**, one per chunk;
- the two existing `ComputeCentroid` tests (`:1400`, `:1412`) continue to pass unchanged.

- [ ] **Step 6: Build and test.**

```bash
dotnet build Iverson.Server/Iverson.Server.slnx
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj
```

- [ ] **Step 7: Commit.**

```bash
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "fix(search): keep degenerate chunk vectors out of the stored centroid"
```

## Tasks NOT in this plan

Inherited from the spec's "Not in this spec". A new spec → new plan cycle is required to add any of these.

- **A threshold-based partial-map policy.** Considered for §4 and rejected: it introduces a tuning constant with nothing to calibrate it against, on a problem that has not been observed.
- **Making `ComputeCentroid` defensive in its own right.** The invariant is enforced at the caller (§3); the function stays pure and total over the input its comment now accurately describes.
- **Any change to the diversifier itself.** `λ`, the incremental `maxSim`, the presence/magnitude split and the seeded argmax are all as part 4b left them.
- **Cross-corpus cluster centroids.** Still deferred, as in 4a and 4b.

## Known issues inherited from spec

**The absent-vector bias is accepted by construction.** As §4 sets out, a candidate whose diversity vector is missing takes no penalty and therefore outranks an otherwise-equal candidate that has a vector and any positive similarity. In a partially-populated chunks collection this quietly favours the un-vectorised points. Both available cures are worse: substituting a value contradicts part 4b's "an absent signal is never replaced" rule and breaks its bit-for-bit `Take(topK)` guarantee, and discarding all diversity whenever any vector is missing throws away a good signal over one bad point. Ben was shown the three options and chose to document rather than code. Worth revisiting only if partial chunk-vector maps are ever observed in production.

**No backfill for already-stored `NaN` centroids.** §3 stops new degenerate centroids from being written but does nothing about any already in Qdrant. For one to exist, the embedding model must have returned an exact zero vector for non-blank text, so there is most likely nothing to backfill — but this could not be verified without querying a live Qdrant instance, so it is stated as a limit rather than claimed as clean. If a document is ever found sitting at the bottom of every result set for no apparent reason, this is the first thing to check.
