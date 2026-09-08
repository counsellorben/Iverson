# SearchSimilar via Chunks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-08-similar-via-chunks-design.md` (commit SHA: `f5105a9`)

**Goal:** Let an operator list types whose `SearchSimilar`, for a chunked property, ranks documents
by chunk retrieval with max-passage collapse, and prove the served ranking reproduces the harness's
collapsed chunk run.

**Architecture:** One list on `VectorRankingOptions`; a routing decision after `SearchSimilar`'s
existing preamble; the middle of `SearchChunks` extracted into a helper both RPCs call; two new read
methods on `IVectorQueryService` for point counts and payload hydration; the head path's response
block shared by both branches. A reproduction check pairs the routed `.similar` run against a
same-binary `.chunks` run over the same raw pool.

**Tech stack:** .NET 10, Qdrant.Client 1.18.1, NSubstitute + xunit (API tests), Testcontainers
(Qdrant integration tests), `Iverson.LoadTest benchmark-query`, `report.py` with `ir_measures`.

---

## Global Constraints

Copied from the spec; every task holds to these.

- **`SearchChunks` is preserved bit for bit.** It gains the shared helper and nothing else; its
  existing tests are the regression guard for Task 3.
- **`LambdaSimilar` stays 1.00.** The routed path applies no chunk-level diversification; the
  collapse would undo it.
- **`SimilarViaChunksTypes` is empty by default.** No proto or client change.
- **Every Qdrant read on the routed path runs under the scoped read key of the collection it reads**
  (`RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(<collection>, readOnly: true))`).
- **No cache and no ceiling on the density read** (spec Known issues).
- **Every command block in Task 5 re-establishes its own shell state.** Steps run in fresh shells;
  no block relies on a variable exported by an earlier one. The repo root is taken from the checkout
  the block runs in (`git rev-parse --show-toplevel`), never hard-coded, so the image is built from
  the branch.

## File Structure

**Modify**
- `Iverson.Server/Iverson.Vector/IVectorRoles.cs` — two methods on `IVectorQueryService`.
- `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs` — their implementations.
- `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs` — the list.
- `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs` — the blank-entry check.
- `Iverson.Server/docker-compose.yml:445-446` — the value-less entry.
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — extractions (Task 3) and the
  routed branch (Task 4).
- `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` §3 — one line on PASS (Task 5).

**Test**
- `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs` — the two new reads.
- `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs` — list binding and validation.
- `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs` — routing, budget,
  collapse, hydration, fallbacks, `SearchChunks` regression.

Campaign artifacts live outside the repo, in
`~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/`.

## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` (and two `critical-design-review`
rounds) at spec-write time and are NOT re-verified here. Trusted as ground truth:

- A1 `VectorRankingOptions` binds via `section.Bind` with a validation site — `ServiceCollectionExtensions.cs:6-36`.
- A2 Compose carries `VectorRanking__*` on `iverson-api` only — `docker-compose.yml:445-446`.
- A3 Type-name lookup is case-insensitive — `SchemaRegistry.cs:11`.
- A4 `SearchSimilar` has `vectorDesc` and can find the chunk descriptor — `ObjectSearchGrpcService.cs:143-158`, `:228-229`.
- A5 `SearchClause`/`SearchLogic` are shared by both requests; `BuildChunksFilter` is static and throws — proto `:34,74,79`; `:870-905`.
- A6 The `SearchChunks` middle is separable at the stated boundaries — `:402-478`.
- A7 Reader interface lacks count/payload retrieve; Qdrant client has both primitives — `IVectorRoles.cs:4-18`; Qdrant.Client 1.18.1.
- A8 `Rerank` yields `(Id, FusedScore)` — `IResultReranker.cs:3-9`.
- A9 Diversifier takes an optional vector and returns `RerankedResult`; λ 1.00 = `Take(topK)` — `IResultDiversifier.cs:8,14`.
- A10 `VectorSearchResult(Id, Score, Payload<string,string>)`; `KeyToUlong` is reachable — `IVectorRoles.cs:49-52`; `IntelligenceStoreConsumer.cs:701`.
- A11 The response block depends only on schema, decision, payload, score, trace id — `:292-323`.
- A12 Collections are per tenant for both chunks and objects — `IntelligenceTenantScope.cs:11`.
- A13 Tests substitute `IVectorQueryService` and construct the service with options — `ObjectSearchGrpcServiceTests.cs:41,74-79`; `VectorRankingOptionsTests.cs:71-99`.
- A14 `BenchmarkDocument.Body` is embedded and chunked; fs-2048 restore is documented; budget is 50 — `BenchmarkDocument.cs:16-18`; `RESTORE.md`; `BenchmarkQueryScenario.cs:42`.
- A15 Nothing rejects a new list key on the section — `ServiceCollectionExtensions.cs:9`.
- A16 Payload by id carries the same columns the search payload does; `ToCanonicalString` is reusable — `IntelligenceVectorService.cs:137-140`.
- A17 Delete removes the object point before the chunks — `IntelligenceStoreConsumer.cs:553`, `:562`.
- A18 Both RPC preambles share one shape — `:133-158`, `:336-356`.
- A19 Ownership and metadata are on chunk points — `IntelligenceStoreConsumer.cs:307-314`; `:368-372`.
- A20 A collection-scoped read key can read that collection's point count — CDR round 1 live probe.
- A21 A chunk's `parent_id` and its parent's object point id agree under `KeyToUlong` — `IntelligenceStoreConsumer.cs:548`, `:310`.
- A22 An empty-string list entry binds as one blank element — CDR round 1 binder run.
- A23 A value-less compose entry is omitted when unset and passed through when set — CDR round 2 probe.
- A24 `RepeatedField<SearchClause>` satisfies `IReadOnlyList<SearchClause>` — `Google.Protobuf` 3.35.1.
- A25 `freshstack-2048-2026-09-07/qrels.trec` holds exactly 672 query ids — CDR round 2 count.

## Verified plan-level assumptions

Newly introduced by this plan and verified 2026-09-08 against `main` `f5105a9`:

| # | Category | Assumption | Evidence |
|---|---|---|---|
| P1 | File path | `QdrantIntegrationTests.cs` and the ranked-changes document exist as cited | `ls`: 16,809 B and 20,036 B |
| P2 | Signature | `QdrantClient.RetrieveAsync(string, IReadOnlyList<PointId>, bool withPayload, bool withVectors, …)` exists; `RetrievedPoint.Id.Num` is `ulong`; `RetrievedPoint.Payload` is `MapField<string, Value>` | Reflection probe against Qdrant.Client 1.18.1 (`net6.0` assembly) |
| P3 | Signature | `CollectionInfo.PointsCount` is non-nullable `ulong` (`HasPointsCount` also present); `GetCollectionInfoAsync(string, CancellationToken)` | Same probe |
| P4 | Code validity | `Telemetry.Source` and `ActivityStatusCode` already in scope in the vector service | `IntelligenceVectorService.cs:17,30` |
| P5 | Consumer impact | `IVectorQueryService` has one production implementer; tests use `Substitute.For<IVectorQueryService>()` | grep: only `IntelligenceVectorService.cs:9`; tests `ObjectSearchGrpcServiceTests.cs:41` |
| P6 | Signature | `ResultReranker.Rerank` returns fused-descending order (the collapse's first-seen rule depends on it) | `ResultReranker.cs:44-45` `.OrderByDescending(r => r.FusedScore)` |
| P7 | Signature | `ResultsById` is a private static helper returning `Dictionary<ulong, VectorSearchResult>` | `ObjectSearchGrpcService.cs:784` |
| P8 | Signature | `decision` is `AuthorizationDecision` (sealed record with `AllowedFields`, `TenantValue`, `OwnershipRequired`, `OwnerValue`); `ChunkDescriptor` is a sealed record with `PropertyName` | `IRowFieldAuthorizationEvaluator.cs:16`; `SchemaDescriptor.cs:112`; `:139`, `:339` |
| P9 | Signature | Generated enum members are `SearchLogic.And` / `.Or`; `SearchSimilarRequest.Filter` / `.FilterLogic` | `obj/…/ObjectSearch.cs:191-194`; `VectorSearchScenario.cs:464` |
| P10 | Signature | `RequestHeaders.Use(string, string)` is `Qdrant.Client.RequestHeaders` (already `using Qdrant.Client`); `MintScopedApiKey(string, bool readOnly)`; `ApplyOwnership(Filter?, bool, string?, string?)` | Qdrant.Client XML `M:Qdrant.Client.RequestHeaders.Use(System.String,System.String)`; `IntelligenceTenantScope.cs:18`; `IntelligenceFilterBuilder.cs:89` |
| P11 | Code validity | `FilterTranslationException` is in scope in the handler | `ObjectSearchGrpcService.cs:181,363` |
| P12 | Signature | Primary-constructor names are `vector`, `logger`, `tenantScope`, `reranker`, `diversifier` | `:34-41` |
| P13 | Command | Both test csproj paths exist; `TESTCONTAINERS_RYUK_DISABLED=false` is the box convention | `ls`; `env` and `~/.testcontainers.properties` both set |
| P14 | Command | Test-count floors are recorded from a run on `main` before Task 1 rather than assumed | (no numeric claim made) |
| P15 | Test convention | `QdrantIntegrationTests` uses `QdrantContainerFixture` exposing a real `IntelligenceVectorService` (`_svc`) and `IntelligenceCollectionManager` (`_mgr`) | `QdrantIntegrationTests.cs:13-48` |
| P16 | Test convention | `BuildConfig(params (string Key, string Value)[])` prefixes `VectorRanking:` to each key — list entries bind as `SimilarViaChunksTypes:0` | `VectorRankingOptionsTests.cs:11-15` |
| P17 | Test convention | Per-test `new ObjectSearchGrpcService(…)` with custom options exists to copy; a `SearchChunks` test stubs `body_vector` on `articles_chunks_test-tenant` with a `parent_id` payload; no existing test asserts the chunk limit `topK × 4` | `ObjectSearchGrpcServiceTests.cs:2613-2620`, `:1452-1456`; grep for a `40` limit assertion: none |
| P18 | Ordering | Task 3 references nothing from Tasks 1–2; Task 4 references Task 1's methods, Task 2's list and Task 3's helpers; Tasks 1 and 2 touch disjoint files | By construction (file structure above) |
| P19 | Command | `--corpus-path`, `--key-map-path`, `--output-dir`, `--config-label`, `--chunk-budget-multiplier` flags; `bench-env.sh`; `/build` on 8081; Qdrant dev key; `iverson-postgres` with user/db `iverson`; `_iverson_schema` table | `Program.cs:416-423`; `bench-env.sh` 329 B; `Program.cs:304`, compose `:436`; compose `:10`; compose `:31-36`; `SchemaRegistryRepository.cs:8` |
| P20 | File path | The fs-2048 snapshot directory holds two `.snapshot` files with the `-6802952876034638` infix the restore loop splits on | `ls freshstack-2048-qdrant-snapshots/` |
| P21 | Commit convention | One-line lowercase imperative subject, `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` trailer | `git log -6` |
| P22 | Sibling set | Every identifier in the plan's code resolves: `RerankedResult(Id, FusedScore)`, `DiversifyCandidate(Id, Score, DiversityVector)`, `OverFetchFactor` is `const ulong`, `SanitizeForLog` is an `Iverson.Api` extension, `KeyToUlong` is `internal static` in `Iverson.Api.Consumers` (same assembly, already `using`) | `IResultReranker.cs:9`; `IResultDiversifier.cs:8`; `:749`; `LoggingExtensions.cs:5`; `IntelligenceStoreConsumer.cs:13,701` |
| P23 | Consumer impact | `BuildChunksFilter` has exactly one caller | `:361` |
| P24 | File path | The ranked-changes document has a `### 3.` heading to add a line under | `docs/2026-09-06-…md:94` |
| P25 | Command | The API logs at `Information` by default, so the "not routed" grep in Task 5 is meaningful | `Iverson.Api/appsettings.json:3-5` |
| P26 | Test convention | No existing API test fixture has a property that is both embedded and chunked; Task 4 registers its own, in the shape of `:1119-1120` | `ObjectSearchGrpcServiceTests.cs:198-199,218-219,1057-1058,1119-1120` |

## Tasks

### Task 1: Vector-service reads

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/IVectorRoles.cs:4-18`
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs` (after `RetrieveNamedVectorAsync`, `:143-176`)
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs`

**Interfaces**
- Produces: `GetPointCountAsync`, `RetrievePayloadAsync` — consumed by Task 4.

- [ ] **Step 0: Record the floors.** On `main`, before any change:
```bash
cd /home/ben/repositories/Iverson
TESTCONTAINERS_RYUK_DISABLED=false dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj 2>&1 | tail -3
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | tail -3
```
Note both passed counts; no later run in this plan may report fewer.

- [ ] **Step 1: The interface.** In `IVectorQueryService`, after `RetrieveNamedVectorAsync`:
```csharp
    /// <summary>Approximate point count of a collection (Qdrant collection info).</summary>
    Task<ulong> GetPointCountAsync(string collectionName);

    /// <summary>
    /// Payload of each listed point, canonicalised to strings exactly as SearchNamedAsync does.
    /// Ids with no point are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>>> RetrievePayloadAsync(
        string collectionName, IReadOnlyList<ulong> ids);
```

- [ ] **Step 2: The implementations.** In `IntelligenceVectorService`, after `RetrieveNamedVectorAsync`:
```csharp
    public async Task<ulong> GetPointCountAsync(string collectionName)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.point_count", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);

        var info = await client.GetCollectionInfoAsync(collectionName);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return info.PointsCount;
    }

    public async Task<IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>>> RetrievePayloadAsync(
        string collectionName, IReadOnlyList<ulong> ids)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.retrieve_payload", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.id_count", ids.Count);

        const int BatchSize = 512;   // same batch as RetrieveNamedVectorAsync

        var result = new Dictionary<ulong, IReadOnlyDictionary<string, string>>();
        foreach (var batch in ids.Chunk(BatchSize))
        {
            var points = await client.RetrieveAsync(
                collectionName,
                batch.Select(id => (PointId)id).ToList(),
                withPayload: true,
                withVectors: false);

            foreach (var p in points)
                result[p.Id.Num] = p.Payload.ToDictionary(kvp => kvp.Key, kvp => ToCanonicalString(kvp.Value));
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        return result;
    }
```

- [ ] **Step 3: Integration tests.** In `QdrantIntegrationTests`, using `_svc` and `_mgr` as the
file's other tests do to create a fresh collection: upsert three points whose payload has a string
field and an integer field; assert `GetPointCountAsync` returns `3`; call `RetrievePayloadAsync` with
two of the ids plus an id that was never written; assert two entries, the integer canonicalised to
its decimal string, and the absent id missing from the dictionary.

- [ ] **Step 4: Run the Vector suite.**
```bash
cd /home/ben/repositories/Iverson
TESTCONTAINERS_RYUK_DISABLED=false dotnet test Iverson.Server/Iverson.Vector.Tests/Iverson.Vector.Tests.csproj 2>&1 | tail -3
```
Green; passed count ≥ Step 0's Vector floor + 2.

- [ ] **Step 5: Commit.**
```bash
git add Iverson.Server/Iverson.Vector/IVectorRoles.cs Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs \
        Iverson.Server/Iverson.Vector.Tests/QdrantIntegrationTests.cs
git commit -m "add point-count and payload reads to IVectorQueryService

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 2: The option

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/VectorRankingOptions.cs` (after `LambdaChunks`)
- Modify: `Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs` (after the `LambdaChunks` range check)
- Modify: `Iverson.Server/docker-compose.yml:445-446`
- Test: `Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs`

**Interfaces**
- Produces: `VectorRankingOptions.SimilarViaChunksTypes` — consumed by Task 4.

- [ ] **Step 1: The property.** After `LambdaChunks`:
```csharp
    // Types whose SearchSimilar, for a chunked property, ranks documents by chunk retrieval with
    // max-passage collapse instead of by the object vector. Empty by default. Enable per type after
    // measuring the corpus: see docs/specs/2026-09-08-similar-via-chunks-design.md §1.
    public List<string> SimilarViaChunksTypes { get; set; } = [];
```

- [ ] **Step 2: The check.** In `AddVectorRanking`, after the `LambdaChunks` range check and before
`services.AddSingleton(Options.Create(opts))`:
```csharp
        for (var i = 0; i < opts.SimilarViaChunksTypes.Count; i++)
        {
            var trimmed = opts.SimilarViaChunksTypes[i]?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                throw new InvalidOperationException(
                    $"{VectorRankingOptions.Section}:SimilarViaChunksTypes[{i}] is blank.");
            opts.SimilarViaChunksTypes[i] = trimmed;
        }
```

- [ ] **Step 3: The compose entry.** In `docker-compose.yml`, beside the two λ entries under
`iverson-api`, value-less (spec §3.1, A23):
```yaml
      - VectorRanking__SimilarViaChunksTypes__0
```

- [ ] **Step 4: Options tests.** In `VectorRankingOptionsTests.cs`, with `BuildConfig` (`:11-15`) and the
`AddVectorRanking` throw pattern (`:71-99`):
  1. Default: no key → `SimilarViaChunksTypes` is empty.
  2. `("SimilarViaChunksTypes:0", "")` → `InvalidOperationException` whose message contains `SimilarViaChunksTypes[0]`.
  3. `("SimilarViaChunksTypes:0", "  BenchmarkDocument ")` → bound list is `["BenchmarkDocument"]`.
  4. `("SimilarViaChunksTypes:0", "BenchmarkDocument")` → round-trips unchanged.

- [ ] **Step 5: Run the Vector suite** (Task 1 Step 4 command). Green; count ≥ floor + 6.

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.Vector/VectorRankingOptions.cs Iverson.Server/Iverson.Vector/ServiceCollectionExtensions.cs \
        Iverson.Server/docker-compose.yml Iverson.Server/Iverson.Vector.Tests/VectorRankingOptionsTests.cs
git commit -m "add SimilarViaChunksTypes to VectorRankingOptions

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 3: Behaviour-preserving extractions in the handler

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`:292-323`, `:356-500`, `:870-915`)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces**
- Produces: `TryBuildChunksFilter`, `SearchChunksFusedAsync` + `ChunkPipeline`, `WriteSimilarResponsesAsync` — consumed by Task 4.

No behaviour change. Every existing test in the file must pass unchanged.

- [ ] **Step 1: Reshape the filter builder.** Change `BuildChunksFilter` (`:870`) to
`(SchemaDescriptor schema, IReadOnlyList<SearchClause> clauses, IReadOnlySet<string>? allowedFields)`;
inside, `if (clauses.Count == 0) return null;` and `foreach (var clause in clauses)`. The one caller
(`:361`) passes `request.Filter`. Add beside it:
```csharp
    private static bool TryBuildChunksFilter(
        SchemaDescriptor schema, IReadOnlyList<SearchClause> clauses, IReadOnlySet<string>? allowedFields,
        out Filter? filter)
    {
        try
        {
            filter = BuildChunksFilter(schema, clauses, allowedFields);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument) { filter = null; return false; }
        catch (FilterTranslationException)                                         { filter = null; return false; }
    }
```

- [ ] **Step 2: Extract the chunk pipeline.** Add a private record and helper; move the `SearchChunks`
body from the vector-name derivation (`:402`) through `reranker.Rerank` (`:478`) into it, **excluding**
the chunk-vector retrieve (`:449-455`):
```csharp
    private sealed record ChunkPipeline(
        IReadOnlyList<VectorSearchResult>   Results,
        IReadOnlyList<RerankedResult>       Fused,
        IReadOnlyDictionary<ulong, float[]> Centroids,
        string ChunksCollection,
        string VectorName);

    private async Task<ChunkPipeline> SearchChunksFusedAsync(
        SchemaDescriptor schema, ChunkDescriptor chunkDesc, AuthorizationDecision decision,
        float[] queryVector, Filter? filter, ulong chunkLimit)
    {
        var vectorName       = chunkDesc.PropertyName.ToSnakeCase() + "_vector";
        var chunksCollection = tenantScope.ResolveCollectionName(schema.CollectionName!, decision.TenantValue, isChunks: true);

        // … today's :412-424 verbatim: scoped key, SearchNamedAsync(chunksCollection, vectorName,
        //     queryVector, chunkLimit, filter), NotFound → results = [] …
        // … today's :425-438 verbatim: distinct parent ids, centroid retrieve with degrade …
        // … today's :457-468 verbatim: decayField, now, candidates …
        var fused = reranker.Rerank(queryVector, candidates);
        return new ChunkPipeline(results, fused, centroids, chunksCollection, vectorName);
    }
```
`SearchChunks` becomes: preamble unchanged through the embedding; then
`var topK = (ulong)Math.Max(1, (int)request.TopK);`,
`var pipeline = await SearchChunksFusedAsync(schema, chunkDesc, decision, queryVector, filter, topK * OverFetchFactor);`,
then the chunk-vector retrieve reading `pipeline.Results`, `pipeline.ChunksCollection`,
`pipeline.VectorName` and `topK`, `var byId = ResultsById(pipeline.Results);`, the diversify projection
over `pipeline.Fused`, and the stream loop unchanged.

- [ ] **Step 3: Extract the response writer.** Move `:292-323` into:
```csharp
    private static async Task WriteSimilarResponsesAsync(
        SchemaDescriptor schema, AuthorizationDecision decision,
        IEnumerable<(IReadOnlyDictionary<string, string> Payload, double Score)> rows,
        string traceId, IServerStreamWriter<SearchResponse> responseStream, CancellationToken ct)
    {
        // … today's columnLookup construction verbatim …
        foreach (var (payload, score) in rows)
        {
            // … today's protoStruct build, MaskDisallowedFields(protoStruct, decision.AllowedFields,
            //     exemptField: "Key"), WriteAsync(new SearchResponse { Data, Score = (float)score,
            //     TraceId = traceId }, ct) verbatim …
        }
    }
```
The head path calls it with the diversified results projected to `(r.Payload, ranked.FusedScore)`,
skipping ids absent from `byId` as today's loop does.

- [ ] **Step 4: One regression test.** In `ObjectSearchGrpcServiceTests`, copying the `SearchChunks`
setup at `:1445-1456`: with `TopK = 10`, assert `_vector.Received(1).SearchNamedAsync("articles_chunks_test-tenant",
"body_vector", Arg.Any<float[]>(), 40UL, Arg.Any<Filter>())` and that the streamed rows are unchanged.

- [ ] **Step 5: Run the API suite.**
```bash
cd /home/ben/repositories/Iverson
dotnet test Iverson.Server/Iverson.Api.Tests/Iverson.Api.Tests.csproj 2>&1 | tail -3
```
Green; count ≥ Step 0's API floor + 1; no existing test modified.

- [ ] **Step 6: Commit.**
```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "extract the chunk pipeline and the SearchSimilar response writer; no behaviour change

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 4: The routed branch

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` (`SearchSimilar`, after the embedding at `:214`)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces**
- Consumes: Task 1's two reads, Task 2's list, Task 3's three helpers.
- Produces: the routed `SearchSimilar` the Task 5 image serves.

- [ ] **Step 1: The branch point.** After the query embedding and before the head path's
`var vectorName = …` (`:221`): compute the chunk descriptor once and replace the `centroidPossible`
expression (`:228-229`) with a null check on it.
```csharp
        var chunkDesc = schema.ChunkFields.FirstOrDefault(c =>
            string.Equals(c.PropertyName, vectorDesc.PropertyName, StringComparison.OrdinalIgnoreCase));

        if (chunkDesc is not null
            && _ranking.SimilarViaChunksTypes.Contains(schema.TypeName, StringComparer.OrdinalIgnoreCase)
            && await TrySearchSimilarViaChunksAsync(schema, chunkDesc, decision, request, queryVector, responseStream, context))
            return;

        var centroidPossible = chunkDesc is not null;
```

- [ ] **Step 2: The routed method.** Returns `false`, having run no chunk search, on every fallback.
```csharp
    private async Task<bool> TrySearchSimilarViaChunksAsync(
        SchemaDescriptor schema, ChunkDescriptor chunkDesc, AuthorizationDecision decision,
        SearchSimilarRequest request, float[] queryVector,
        IServerStreamWriter<SearchResponse> responseStream, ServerCallContext context)
    {
        bool NotRouted(string reason)
        {
            logger.LogInformation("[SearchSimilar] type={Type} not routed via chunks: {Reason}",
                request.TypeName.SanitizeForLog(), reason);
            return false;
        }

        // Spec §3.2 condition 3: the logic test lives here — BuildChunksFilter never reads it, and
        // SearchChunks (which must stay bit-for-bit) silently ANDs an OR request today.
        if (request.Filter.Count > 1 && request.FilterLogic != SearchLogic.And)
            return NotRouted("filter logic is not AND");
        if (!TryBuildChunksFilter(schema, request.Filter, decision.AllowedFields, out var filter))
            return NotRouted("filter is not chunk-expressible");

        // Condition 4: both counts readable and positive.
        var objectCollection = tenantScope.ResolveCollectionName(schema.CollectionName!, decision.TenantValue, isChunks: false);
        var chunksCollection = tenantScope.ResolveCollectionName(schema.CollectionName!, decision.TenantValue, isChunks: true);
        ulong objectCount, chunkCount;
        try
        {
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(objectCollection, readOnly: true)))
                objectCount = await vector.GetPointCountAsync(objectCollection);
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(chunksCollection, readOnly: true)))
                chunkCount = await vector.GetPointCountAsync(chunksCollection);
        }
        catch (RpcException ex)
        {
            return NotRouted($"counts unavailable: {ex.Status.Detail}");
        }
        if (objectCount == 0) return NotRouted("empty object collection");
        if (chunkCount == 0)  return NotRouted("empty chunks collection");

        filter = IntelligenceFilterBuilder.ApplyOwnership(
            filter, decision.OwnershipRequired, schema.Authorization?.OwnerField?.ToCamelCase(), decision.OwnerValue);

        // Spec §3.4: topK × ceil(chunks/doc) × the existing over-fetch.
        var topK         = (ulong)Math.Max(1, (int)request.TopK);
        var chunksPerDoc = (ulong)Math.Ceiling((double)chunkCount / objectCount);
        var pipeline     = await SearchChunksFusedAsync(
            schema, chunkDesc, decision, queryVector, filter, topK * chunksPerDoc * OverFetchFactor);

        // Spec §3.5.1: max-passage collapse. Rerank output is fused-descending (ResultReranker.cs:44-45),
        // so the first sighting of a parent is its best chunk; ties keep first-seen order.
        var byId      = ResultsById(pipeline.Results);
        var seen      = new HashSet<ulong>();
        var collapsed = new List<DiversifyCandidate>();
        foreach (var fused in pipeline.Fused)
        {
            if (!byId.TryGetValue(fused.Id, out var r)) continue;
            if (!r.Payload.TryGetValue("parent_id", out var parentKey) || string.IsNullOrEmpty(parentKey)) continue;
            var parentId = IntelligenceStoreConsumer.KeyToUlong(parentKey);
            if (!seen.Add(parentId)) continue;
            collapsed.Add(new DiversifyCandidate(
                parentId, fused.FusedScore,
                pipeline.Centroids.TryGetValue(parentId, out var centroid) ? centroid : null));
        }

        // Spec §3.5.2: document-level diversification with LambdaSimilar on the centroid.
        var selected = diversifier.Diversify(collapsed, (int)topK, _ranking.LambdaSimilar);

        // Spec §3.5.3: hydrate the top parents from the object collection.
        IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>> payloads =
            new Dictionary<ulong, IReadOnlyDictionary<string, string>>();
        if (selected.Count > 0)
        {
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(objectCollection, readOnly: true)))
            {
                try
                {
                    payloads = await vector.RetrievePayloadAsync(objectCollection, selected.Select(s => s.Id).ToList());
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound) { }
                catch (RpcException ex)
                {
                    throw new RpcException(new Status(StatusCode.Unavailable, $"Vector store unavailable: {ex.Status.Detail}"));
                }
            }
        }

        // Spec §3.5.4 / §3.6: a parent missing at hydration is skipped and logged.
        var rows = new List<(IReadOnlyDictionary<string, string> Payload, double Score)>();
        foreach (var s in selected)
        {
            if (payloads.TryGetValue(s.Id, out var payload)) rows.Add((payload, s.FusedScore));
            else logger.LogWarning("[SearchSimilar] parent {Id} missing at hydration; skipped", s.Id);
        }
        await WriteSimilarResponsesAsync(schema, decision, rows, request.TraceId, responseStream, context.CancellationToken);
        return true;
    }
```

- [ ] **Step 3: Tests.** In `ObjectSearchGrpcServiceTests`, each test constructing its own service as
`:2613-2620` does, with `Options.Create(new VectorRankingOptions { LambdaSimilar = 1.00, LambdaChunks = 0.70,
SimilarViaChunksTypes = ["Article"] })`. No existing fixture has a property that is both embedded
and chunked (`:218-219` embeds `Name` and chunks `Secret`; `:1119-1120` embeds `Title` and chunks
`Body`), so register a schema for these tests in the shape of `:1119-1120` with
`VectorFields = [new VectorDescriptor("Body", 1024, "snowflake-arctic-embed:s")]` and
`ChunkFields = [new ChunkDescriptor("Body", 512, 64, "snowflake-arctic-embed:s", 1024)]`, requesting
`Property = "Body"`. Stub
`_vector.GetPointCountAsync("articles_test-tenant")` and `_vector.GetPointCountAsync("articles_chunks_test-tenant")`,
`_vector.SearchNamedAsync("articles_chunks_test-tenant", "body_vector", …)` returning chunk results with
`parent_id` payloads, `_vector.RetrieveNamedVectorAsync(…, "body_centroid")` returning an empty
dictionary, and `_vector.RetrievePayloadAsync("articles_test-tenant", …)` returning payload dictionaries.
  1. **Routed, density 13/10** (`ceil` → 2), `TopK = 10`: chunk search received with limit `80UL`;
     `SearchNamedAsync("articles_test-tenant", …)` never called; `RetrievePayloadAsync` received with
     exactly the top-10 collapsed ids in order; streamed rows carry the collapsed scores in order.
  2. **Routed, density 108/10** (→ 11): limit `440UL`.
  3. **Collapse:** two chunks of one parent with scores 0.9 and 0.5 → the parent appears once with 0.9.
  4. **Missing parent at hydration:** `RetrievePayloadAsync` omits one selected id → stream has one row
     fewer; the others unchanged.
  5. **Not routed** (each asserting the object search ran and the chunk search did not): unlisted type;
     listed type whose property is embedded but not chunked; a non-EQUALS clause; a scalar-column
     clause; `FilterLogic = SearchLogic.Or` over two clauses; `GetPointCountAsync` throwing
     `RpcException(Unavailable)`; object count 0; chunk count 0.
  6. **Ownership:** an owner-restricted principal → the chunk search's `Filter` contains the owner
     `MatchKeyword`, as the existing `SearchChunks` ownership test asserts.
  7. **Field masking:** a field-restricted principal → the routed response omits the disallowed column
     and keeps `Key`.

- [ ] **Step 4: Run the API suite** (Task 3 Step 5 command). Green; count ≥ floor + 15.

- [ ] **Step 5: Commit.**
```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "route SearchSimilar through chunk retrieval for listed types

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

### Task 5: Reproduction check and record

**Files:**
- Modify (on PASS only): `docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` §3

Artifacts in `~/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07/`.

**Interfaces**
- Consumes: the branch at Task 4's commit.
- Produces: `runs/fs2048-routed.{similar,chunks}.trec`, `report-routed.txt`, and the verdict.

- [ ] **Step 1: Restore the arm.**
```bash
K=dev-only-not-for-production-qdrant-key-0123456789
for c in benchmark_documents_tenant_bypass benchmark_documents_chunks_tenant_bypass; do
  curl -s -X DELETE -H "api-key: $K" 127.0.0.1:6333/collections/$c; echo
done
cd /home/ben/repositories/iverson-benchmark-corpora/freshstack-2048-qdrant-snapshots
for f in *.snapshot; do
  c="${f%%-6802952876034638*}"
  curl -s -X POST -H "api-key: $K" "http://localhost:6333/collections/$c/snapshots/upload?priority=snapshot" -F "snapshot=@$f"; echo
done
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_chunks_tenant_bypass | grep -o '"points_count":[0-9]*'   # 18622
curl -s -H "api-key: $K" 127.0.0.1:6333/collections/benchmark_documents_tenant_bypass        | grep -o '"points_count":[0-9]*'   # 6000
docker exec iverson-postgres psql -U iverson -d iverson -c "DELETE FROM _iverson_schema WHERE type_name = 'BenchmarkDocument';"
```
Any other count: stop.

- [ ] **Step 2: Build from the branch; start with both variables.** Run from inside the branch checkout.
```bash
R=$(git rev-parse --show-toplevel)
cd $R/Iverson.Server
docker compose build iverson-api 2>&1 | tail -2
VectorRanking__SimilarViaChunksTypes__0=BenchmarkDocument VECTOR_RANKING_LAMBDA_CHUNKS=1.00 \
  docker compose up -d --no-deps iverson-api
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
curl -s 127.0.0.1:8081/build | grep -o '"composite":"[^"]*"'
docker inspect iverson-api | grep -o '"VectorRanking__[A-Za-z0-9_]*=[^"]*"'
# must show SimilarViaChunksTypes__0=BenchmarkDocument, LambdaChunks=1.00, LambdaSimilar=1.00
```

- [ ] **Step 3: The one run.** `DocumentBudget` is 50, so `SearchChunks` requests 200 (server raw
fetch 800) and the routed `SearchSimilar` fetches 50 × 4 × 4 = 800 at 3.10 chunks/doc.
```bash
R=$(git rev-parse --show-toplevel)
B=/home/ben/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07
source /home/ben/iverson-benchmark-data/bench-env.sh
cd $R/Iverson.Server/Iverson.LoadTest && dotnet run -c Release -- benchmark-query \
  --corpus-path $B --key-map-path $B/keymap.json --output-dir $B/runs \
  --config-label fs2048-routed --chunk-budget-multiplier 4 2>&1 | tee $B/runs/fs2048-routed.log
wc -l $B/runs/fs2048-routed.similar.trec $B/runs/fs2048-routed.chunks.trec   # 33600 each
grep -c "REFUSING: chunk budget" $B/runs/fs2048-routed.log                   # 0
docker logs iverson-api 2>&1 | grep -c "not routed via chunks"              # 0
```

- [ ] **Step 4: Report.** The rule's pairing first; the Tier 1 pairing appended for continuity only.
```bash
R=$(git rev-parse --show-toplevel)
B=/home/ben/repositories/iverson-benchmark-corpora/freshstack-2048-2026-09-07
export PYTHONPATH=/home/ben/repositories/iverson-benchmark-corpora/python-libs
cd $R/Iverson.Server/Iverson.LoadTest/scripts
python3 report.py --qrels $B/qrels.trec --baseline $B/runs/fs2048-routed.chunks.trec \
  --run $B/runs/fs2048-routed.similar.trec 2>&1 | tee $B/report-routed.txt
python3 report.py --qrels $B/qrels.trec --baseline $B/runs/fs-2048-l070.chunks.trec \
  --run $B/runs/fs2048-routed.similar.trec 2>&1 | tee -a $B/report-routed.txt
```
Spec §5: **PASS** if the first pairing shows 672 / 672 covered, no duplicate doc ids, no `BUILD
MISMATCH`, and nDCG@10 and R@50 deltas within ±0.005. The second pairing may print `BUILD MISMATCH`;
it is recorded, not gated.

- [ ] **Step 5: Restore the box.** From a shell **without** either variable:
```bash
R=$(git rev-parse --show-toplevel)
cd $R/Iverson.Server && docker compose up -d --no-deps iverson-api
until curl -sf 127.0.0.1:8081/build >/dev/null; do sleep 3; done
docker inspect iverson-api | grep -o '"VectorRanking__[A-Za-z0-9_]*=[^"]*"'   # LambdaChunks=0.70; no SimilarViaChunksTypes entry
```

- [ ] **Step 6: Record.** On **PASS**: add one line under §3 of
`docs/2026-09-06-ranked-changes-after-retrieval-experiments.md` naming `VectorRanking:SimilarViaChunksTypes`,
the `fs-2048` arm, and the two deltas from the first pairing, and commit:
```bash
git add docs/2026-09-06-ranked-changes-after-retrieval-experiments.md
git commit -m "record the SearchSimilar-via-chunks reproduction check

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```
On **FAIL**: no commit; the branch stays unmerged, and diagnosis starts with the budget (the routed
raw pool versus the pool the baseline fused, and the 200-versus-800 collapse boundary) before the
harness pipeline (spec §6).

## Tasks NOT in this plan

Inherited from the spec's Out of scope:

- `SearchChunks`. Its behaviour is preserved bit for bit; it gains a shared helper, nothing else.
- Any proto or client change. The option is server configuration (Ben, 2026-09-08).
- A cache or ceiling on the density read. Neither is shown to be needed; see Known issues.
- Chunk-level diversification on the routed path. The collapse undoes it.
- A new gate rule. §4 checks that the served ranking reproduces a same-binary collapsed chunk run
  built over the same raw pool, within a tolerance.

## Known issues inherited from spec

- **No ceiling on the chunk limit.** A type with pathological density (hundreds of chunks per
  document) and a large `top_k` produces a large Qdrant limit. It is slow, not wrong. Add a ceiling
  only when a deployment shows one is needed.
- **Two collection-info calls per routed request.** Accepted for now; a cache is deferred until
  measured latency says otherwise.
- **The routed and head paths rank differently for the same type** depending on the filter, because
  a non-expressible filter falls back. Chosen over rejection so enabling the option never breaks an
  existing caller (Ben, 2026-09-08).
- **`points_count` includes points of every field.** A type with two chunked properties counts
  both properties' chunks in one collection, over-estimating density for either property alone.
  The budget is then larger than needed, never smaller.
