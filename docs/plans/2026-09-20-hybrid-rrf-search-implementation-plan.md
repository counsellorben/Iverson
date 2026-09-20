# Hybrid RRF Search (experimental) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-17-hybrid-rrf-search-design.md` (commit SHA: `9f1e8e5c9f26f7b13141b7eafe7bd18a48732448`)

**Goal:** Give `SearchSimilar` and `SearchChunks` an optional, experimental `hybrid` request field that fuses the existing Qdrant ranking with a StarRocks BM25 ranking and/or a StarRocks filter+sort ranking, by Reciprocal Rank Fusion, with `hybrid` absent reproducing today's behaviour bit-for-bit.

**Architecture:** Four independent layers. (1) `object_search.proto` gains a `HybridRank` message and a `hybrid` field on both request messages. (2) `IEngagementStoreSearchService`/`EngagementRepository` gain two key-only read methods, backed by two new `StarRocksQueryBuilder` builders (`BuildLexicalRank`: a hand-rolled BM25 CTE, since StarRocks 4.1.1 has no relevance-scoring function; `BuildRankQuery`: the caller's filter+sort, extended with the request's hard filter as its own AND group and a key tie-breaker). (3) A new pure, I/O-free `Iverson.Vector.ReciprocalRankFusion` (`Fuse` + `RescaleOnto`) and `Iverson.Vector.HybridTokenizer`. (4) `ObjectSearchGrpcService` wires it all together: validation, one shared StarRocks-fetch helper, and per-RPC fusion at all three ranking sites (SearchSimilar's head path, its chunks-routed path, and SearchChunks).

**Tech stack:** .NET 10, Dapper 2.1.86, MySqlConnector 2.4.0, xUnit, NSubstitute, FluentAssertions, DotNet.Testcontainers (StarRocks `allin1-ubuntu:4.1.1`, pinned by `StarRocksImage.Tag`).

All paths below are relative to `Iverson.Server/`, and every `dotnet` command runs from `Iverson.Server/`, except the proto file (`Iverson.Clients/Common/Proto/object_search.proto`, relative to the repo root) and the Commit steps' `git add` paths (also repo-root-relative).

---

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time and by `critical-design-review` round 1 (✅ approved as-is, 0 findings). Trusted as ground truth; evidence for each is in the spec's own "Verified assumptions" table (§10) under the same number.

1. `EngagementRepository.RunTenantScopedAsync` + `IsExpectedMissingResourceError` is the tenant-scoped read helper all 4 existing read paths share (A1).
2. `StarRocksQueryBuilder.BuildWhere` takes clauses, logic, params, authz and is reusable (A2).
3. `AuthorizationConstraint(AllowedFields, OwnerColumn, OwnerValue, TenantColumn, TenantValue)` carries both tenant and ownership; `BuildSearch` ANDs both as separate groups (A3).
4. `EvaluateAuthorization` builds the constraint dictionary from an `AuthorizationDecision`; SearchSimilar/SearchChunks can do the same from their own `decision` (A4).
5. Every non-key property is a StarRocks scalar column, FKs included (A5) — except: StarRocks and Qdrant diverge on an empty `IN` list, which is why hybrid rejects it (§3.2 of the spec).
6. Chunk-filter properties (key + `[IversonMetadata]`) are StarRocks columns too (A6).
7. `BuildSearch` ANDs owner and tenant as separate parenthesised groups (A7).
8. `BuildOrder` throws on a resolved-but-unauthorized sort field, but silently skips an unresolvable one — the design's validation table exists because of this asymmetry (A8).
9. The StarRocks key column is the entity's Guid string; `IntelligenceStoreConsumer.KeyToUlong` derives the same id from it as from a Qdrant point id (A9).
10. `IVectorRoles.RetrievePayloadAsync` canonicalises payloads the same way `SearchNamedAsync` does (A10).
11. Regex patterns pass through MySqlConnector as bound parameters (A11 — re-verified below with the exact production string, not just literals; see plan assumption 4).
12. `regexp_count(lower(c), '\w+')` is the document-length tokenizer (A12).
13. StarRocks `\w`/`\b` are ASCII-only while `lower()` is Unicode-aware, which is why the tokenizer is ASCII-only (A13).
14. `EngagementStoreConsumer` upserts the full payload to StarRocks, so `BenchmarkDocument.Body` reaches it; whether the *preserved* benchmark volumes hold those rows is unchecked and is a documented run precondition, not code (A14, spec §7).
15. StarRocks list depth is capped at `EngagementQueryLimitOptions.MaxPageSize` (default 1000) because `CheckPageSize` rejects anything larger (A15).
16. The harness's flag + `<label>.meta.json` sidecar pattern is `BenchmarkQueryScenario.cs:206-222` (A16).
17. Field 8 and the name `HybridRank` are free on both request messages (A17).
18. The .NET contracts project and the Java client compile the proto at build time; Go/Python/TS are checked-in generated code that proto3 ignores an unknown field on (A18).
19. Nothing outside the .NET server and the LoadTest harness uses the field (A19).
20. `EngagementRepository` and `DisabledEngagementStoreSearchService` are the only two implementers of `IEngagementStoreSearchService`; every test substitutes it with NSubstitute (A20).
21. `EngagementNotReadyException` fires only when the circuit is open; any other StarRocks failure is a raw exception that must be explicitly mapped, or it surfaces as `Unknown` (A21).
22. The request's hard filter, tenant, and ownership apply to every StarRocks list; the only divergence found (empty `IN`) is rejected; the chunks paths are `EQUALS`-only so it can't reach them (A22).
23. StarRocks-only hits need their centroids fetched too, or an operator-set `LambdaSimilar < 1` gives them no MMR penalty (A23).
24. The CTE + aggregate `CROSS JOIN` BM25 shape runs correctly on StarRocks 4.1.1 (A24 — re-verified below with bound parameters, not just literals; see plan assumption 4).

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | None of the 5 new files exist yet, and none of the new type/method names collide | `find Iverson.Vector Iverson.Vector.Tests Iverson.StarRocks.Tests -iname "ReciprocalRankFusion*" -o -iname "HybridTokenizer*" -o -iname "HybridRankIntegrationTests*"` → nothing. `grep -rn "ReciprocalRankFusion\|HybridTokenizer\|BuildLexicalRank\|BuildRankQuery\|SearchLexicalAsync\|SearchRankQueryAsync\|FetchHybridRanksAsync\|ValidateHybridRequest\|HybridRanks\b" --include=*.cs` → no hits anywhere in the repo |
| 2 | Signature | `AuthorizationConstraint`'s positional constructor order is `(AllowedFields, OwnerColumn, OwnerValue, TenantColumn = null, TenantValue = null)`, matching `new AuthorizationConstraint(decision.AllowedFields, decision.OwnerFieldName, decision.OwnerValue, decision.TenantColumn, decision.TenantValue)` in Task 5 | `Iverson.StarRocks/AuthorizationConstraint.cs:3-8`; `AuthorizationDecision` field names at `Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs:16-59` (`OwnerFieldName`, `OwnerValue`, `AllowedFields`, `TenantColumn`, `TenantValue`) |
| 3 | Code validity | `EngagementQuerySchema.KeyColumnName`/`.ColumnNames`/`.IsTenantColumn` and `TenantIdentifier.Qualify`/`DatabaseName` are the exact members the new builders need | `Iverson.StarRocks/EngagementQuerySchema.cs:33-47`; existing `BuildSearch` calls at `StarRocksQueryBuilder.cs:34-104` use the same members identically |
| 4 | Code validity (run) | A C# bound parameter (`param.Add(pName, $@"\b{token}\b")`, single backslash) matches correctly via `regexp_count`, and the full `BuildLexicalRank` CTE shape (owner+tenant AND groups, `CROSS JOIN`, per-token `tf`/`df`, `ORDER BY` on an unaliased expression) runs end-to-end against `starrocks/allin1-ubuntu:4.1.1` with bound parameters, honours the tenant/owner filter, and excludes the wrong-tenant/zero-match rows | Live run (this session) against a throwaway `allin1-ubuntu:4.1.1` container via MySqlConnector 2.4.0 + Dapper 2.1.86: single-backslash bound param on `'the quick brown fox jumps'` matched id=1, on `'foxglove is a plant'` did NOT match (word-boundary correct); double-backslash bound param matched nothing. Full CTE against a 5-row `pp2.d` table (owner `u1`/tenant `t1` vs `t1`/`t2`) with 2 tokens returned `ranked keys: a,b,c` — excluded row `e` (tenant `t2`) and row `d` (0 matches) correctly |
| 5 | Code validity | Dapper's `QueryAsync<string>` on a single-column `SELECT` binds the value regardless of its SQL alias (ordinal, not name-based), so `BuildLexicalRank`'s `SELECT s.__key` and `BuildRankQuery`'s `SELECT` `` `{key}` `` can both be read with `conn.QueryAsync<string>(sql, param)` | Confirmed by the same live run in row 4 — the query text aliases the column `__key`, and `QueryAsync<string>` returned the values directly, no dynamic/dictionary indexing needed |
| 6 | Code validity | `RerankedResult` is a `record`, so `r with { FusedScore = x }` compiles | `Iverson.Vector/IResultReranker.cs:10`: `public sealed record RerankedResult(ulong Id, double FusedScore);` |
| 7 | Code validity | `Math.Min` has a `(ulong, ulong)` overload | BCL — `System.Math.Min(ulong, ulong)` has existed since .NET Core 2.0; no other `ulong` arithmetic in this codebase (e.g. `OverFetchFactor`/`topK` math at `ObjectSearchGrpcService.cs:245-248`) works around its absence |
| 8 | Code validity | No project touched by this plan sets `TreatWarningsAsErrors`/`WarningsAsErrors`, so nullable-flow-analysis narrowing (`chunkDesc.PropertyName` after its earlier `if (chunkDesc is null) return NotRouted(...)`) can't turn a narrowing edge case into a build break | `grep -rn "TreatWarningsAsErrors\|WarningsAsErrors" Iverson.Api/Iverson.Api.csproj Iverson.Vector/Iverson.Vector.csproj Iverson.StarRocks/Iverson.StarRocks.csproj Iverson.Api.Tests/*.csproj Iverson.Vector.Tests/*.csproj Iverson.StarRocks.Tests/*.csproj Iverson.LoadTest/*.csproj` → no hits; no `Directory.Build.props` at `Iverson.Server/` or repo root |
| 9 | Code validity | `chunkDesc.PropertyName` (no `!`) is already used unconditionally later in the SAME method body after the same null-check, so the plan's usage matches the file's own established pattern | `ObjectSearchGrpcService.cs:389-390` (existing): `SearchChunksFusedAsync(schema, chunkDesc, decision, queryVector, filter, ...)` passes `chunkDesc` (typed `ChunkDescriptor` non-nullable parameter) with no `!`, from the same scope this plan adds code to |
| 10 | File path / line | Every Modify target's current line range is exactly as cited in Tasks 5-7 | `grep -n` against the live file (this session): `SearchSimilar` header `151`, `ResolveTopK` `181`, candidates block `305-334`, `TrySearchSimilarViaChunksAsync` header `342`, collapse block `392-409`, `SearchChunks` header `495`, its `ResolveTopK` `524`, `SearchChunksFusedAsync` call `574`, its diversityCandidates block `595-624`; direct `Read` of each range confirmed the exact text below |
| 11 | File path / line | `object_search.proto`'s `SearchSimilarRequest` spans lines 101-109 and `SearchChunksRequest` spans 113-124, both ending in `filter_logic = 7;` | `grep -n "message SearchSimilarRequest\|message SearchChunksRequest\|^}"` against the live file (this session) |
| 12 | Command | `Iverson.LoadTest` already depends on `MySqlConnector`/`Dapper` (no new package needed for the precondition comment's referenced pattern) but has NO Qdrant client package — a code-enforced precondition would be a new dependency the spec doesn't ask for, so Task 8 documents the precondition instead of enforcing it | `Iverson.LoadTest/Iverson.LoadTest.csproj` PackageReferences (Dapper, MySqlConnector, no Qdrant.Client); `DirectSeeder.cs:38` already does `new MySqlConnection(config.StarRocksCs)` |
| 13 | Signature | `CommandFlags`'s boolean-flag convention is presence-only (`args.Contains("--flag")`), matching how `--hybrid-lexical` should be added | `Program.cs:465`: `ForceReseed = args.Contains("--force-reseed")` |
| 14 | Consumer impact | Adding 2 methods to `IEngagementStoreSearchService` breaks no existing test: every existing test substitutes the interface via NSubstitute (auto-implements unstubbed members) and every existing request has `hybrid` unset, so the new methods are never invoked by any pre-existing test path | `grep -rln ": IEngagementStoreSearchService\|Substitute.For<IEngagementStoreSearchService>"` → `EngagementRepository.cs` (real impl), `DisabledEngagementStoreSearchService.cs` (real impl), and 5 test files, all via `Substitute.For<IEngagementStoreSearchService>()`; no reflection-based mock elsewhere |
| 15 | Ordering | Task 1 (proto) has no compile dependency on Tasks 2-4; Tasks 2-3 (StarRocks) have no compile dependency on Task 1 or 4; Task 4 (fusion) has no dependency on any other task; Tasks 5-7 (gRPC) depend on 1, 2/3, and 4; Task 8 (harness) depends only on Task 1 | `BuildLexicalRank`/`BuildRankQuery`/the interface additions use only `SearchQuery`/`SearchClause`/`SearchLogic` (pre-existing proto types), never `HybridRank`; `ReciprocalRankFusion`/`HybridTokenizer` use only `ulong`/`string`/`RerankedResult` (no proto types at all); confirmed by reading each new file's design against its own imports below |
| 16 | Command | `dotnet test <Project> --filter "..."` run from `Iverson.Server/` is the established invocation, and each project name below is a real `.csproj` | `docs/plans/2026-09-15-projection-tenant-resolution-implementation-plan.md` assumption 7; `find Iverson.Server -maxdepth 1 -iname "Iverson.Vector.Tests" -o -iname "Iverson.StarRocks.Tests" -o -iname "Iverson.Api.Tests"` → all three exist as directories with matching `.csproj` files |
| 17 | Consumer impact | `EngagementQueryTranslationException`, `EngagementStoreDisabledException`, `EngagementNotReadyException` are all plain `Iverson.StarRocks` exception types already imported into `ObjectSearchGrpcService.cs` via `using Iverson.StarRocks;`, so Task 5's shared catch block needs no new `using` | `ObjectSearchGrpcService.cs:8` `using Iverson.StarRocks;`; existing `Search` RPC already catches all three by these exact names (read at plan-drafting time) |
| 18 | Consumer impact | `SearchOperator.In` and `SearchValue.KindOneofCase.StringList` are the exact generated C# names Task 5's empty-IN check needs | `Iverson.Vector/IntelligenceFilterBuilder.cs`: `SearchOperator.In => (...)` and `clause.Value.KindCase != SearchValue.KindOneofCase.StringList` (existing code, same enum) |
| 19 | Code validity (run) | `IntelligenceStoreConsumer.KeyToUlong`'s Guid -&gt; ulong mapping, needed to construct Task 7's fixed-id test Guids | Live run (this session): `Guid.Parse("00000000-0000-0000-0100-000000000000").ToByteArray()` sliced at `[8..15]` and read little-endian -&gt; `1`; `...-0200-...` -&gt; `2`; `...-6300-...` -&gt; `99`. A first draft used `...-0000-0000000000NN`, which does NOT round-trip to `NN` (it round-trips to a number in the 10^16-10^19 range) — caught and corrected in this same verification pass before the test code above was finalized |

## File Structure

**Create**
- `Iverson.Vector/ReciprocalRankFusion.cs`: `Fuse` (RRF over the vector list plus up to 2 StarRocks rank dictionaries) and `RescaleOnto` (linear rescale onto the vector pool's own score range).
- `Iverson.Vector/HybridTokenizer.cs`: the ASCII query-side tokenizer.
- `Iverson.Vector.Tests/ReciprocalRankFusionTests.cs`
- `Iverson.Vector.Tests/HybridTokenizerTests.cs`
- `Iverson.StarRocks.Tests/HybridRankIntegrationTests.cs`: container tests for `BuildLexicalRank`/`BuildRankQuery` via `EngagementRepository`.

**Modify**
- `Iverson.Clients/Common/Proto/object_search.proto`: `HybridRank` message; `hybrid = 8` on both request messages.
- `Iverson.StarRocks/StarRocksQueryBuilder.cs`: `BuildLexicalRank`, `BuildRankQuery`, `AppendOwnerAndTenant`, `BuildStrictOrder`.
- `Iverson.StarRocks/IEngagementStoreRoles.cs`: 2 new `IEngagementStoreSearchService` methods.
- `Iverson.StarRocks/EngagementRepository.cs`: their implementations.
- `Iverson.StarRocks/DisabledEngagementStoreSearchService.cs`: their throwing stubs.
- `Iverson.Api/Grpc/ObjectSearchGrpcService.cs`: `ValidateHybridRequest`, `BuildStarRocksAuthz`, `HybridRanks`, `ToRankDictionary`, `FetchHybridRanksAsync`; wiring in `SearchSimilar`, `TrySearchSimilarViaChunksAsync`, `SearchChunks`.
- `Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs` and `Iverson.LoadTest/Program.cs`: `--hybrid-lexical` flag, meta.json key, precondition comment.

**Test (modify)**
- `Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`
- `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

## Tasks

### Task 1: Proto — `HybridRank`

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_search.proto:100-124`

**Interfaces:**
- Produces: `HybridRank` (fields `lexical`, `rank_query`) and `hybrid` field 8 on both request messages. Every later task consumes this.

- [ ] **Step 1: Add the message and the two fields**

In `Iverson.Clients/Common/Proto/object_search.proto`, insert immediately before line 101 (`message SearchSimilarRequest {`):

```proto
// Experimental. Present = fuse the vector ranking with one or two StarRocks rankings by
// Reciprocal Rank Fusion. Absent = today's behaviour, bit-for-bit. See
// docs/specs/2026-09-17-hybrid-rrf-search-design.md.
message HybridRank {
    bool        lexical    = 1;  // add a BM25 ranking over the searched property's text
    SearchQuery rank_query = 2;  // add a ranking: rows matching clauses, in sort order (soft boost)
}

```

Then change line 108 (the last field of `SearchSimilarRequest`, currently `    SearchLogic           filter_logic = 7;   // default AND`) so the message reads:

```proto
message SearchSimilarRequest {
    string type_name = 1;  // registered entity type
    string property  = 2;  // property with [IversonEmbedding]
    string query     = 3;  // text to embed and compare
    uint32 top_k     = 4;
    string trace_id  = 5;
    repeated SearchClause filter        = 6;  // optional scalar/FK filter, ANDed/ORed with filter_logic
    SearchLogic           filter_logic = 7;   // default AND
    HybridRank             hybrid       = 8;   // experimental — see HybridRank
}
```

And `SearchChunksRequest` (currently ending at line 124 with `    SearchLogic           filter_logic = 7;`):

```proto
message SearchChunksRequest {
    string type_name = 1;  // registered entity type
    string property  = 2;  // property with [IversonChunk]
    string query     = 3;  // text to embed and compare
    uint32 top_k     = 4;
    string trace_id  = 5;
    // EQUALS clauses (ANDed) on the type's primary-key property and/or its metadata columns
    // (properties annotated [IversonMetadata]). Other operators, other properties, and MUST_NOT
    // clauses are rejected. See ObjectSearchGrpcService.SearchChunks.
    repeated SearchClause filter        = 6;
    SearchLogic           filter_logic = 7;
    HybridRank             hybrid       = 8;   // experimental — see HybridRank
}
```

- [ ] **Step 2: Build to regenerate the .NET contracts**

```bash
cd Iverson.Server
dotnet build Iverson.Api/Iverson.Api.csproj
```

This is a compile-only check — `Iverson.Client.Contracts.csproj` regenerates `HybridRank`/`SearchSimilarRequest.Hybrid`/`SearchChunksRequest.Hybrid` from the `<Protobuf Include="../../Common/Proto/*.proto" .../>` item at build time (plan assumption verified against spec A18). Expect it to fail here, since nothing references the new field yet, if — and only if — a later task's code isn't in place; at this point in the plan it should succeed with no consumers at all.

- [ ] **Step 3: Commit**

```bash
git add -f Iverson.Clients/Common/Proto/object_search.proto
git commit -m "add HybridRank to object_search.proto: hybrid field 8 on SearchSimilarRequest and SearchChunksRequest

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 2: StarRocks query builders

**Files:**
- Modify: `Iverson.StarRocks/StarRocksQueryBuilder.cs`
- Test: `Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs`

**Interfaces:**
- Produces: `StarRocksQueryBuilder.BuildLexicalRank(...)`, `StarRocksQueryBuilder.BuildRankQuery(...)` — both `internal static (string Sql, DynamicParameters Param)`. Task 3 consumes both.

- [ ] **Step 1: Write the failing tests**

Append to `Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs` (before its closing `}`):

```csharp
    // ── BuildLexicalRank ─────────────────────────────────────────────────────

    [Fact]
    public void BuildLexicalRank_BindsOneParameterPerTokenAsWordBoundaryPattern()
    {
        var (_, param) = StarRocksQueryBuilder.BuildLexicalRank(
            "articles", ArticleSchema(), "Body", ["fox", "brown"],
            hardFilter: null, SearchLogic.And, depth: 50, authz: null);

        param.Get<string>("__tok0").Should().Be(@"\bfox\b");
        param.Get<string>("__tok1").Should().Be(@"\bbrown\b");
    }

    [Fact]
    public void BuildLexicalRank_ProjectsOnlyTheKeyColumnAndOrdersByScoreThenKey()
    {
        var (sql, _) = StarRocksQueryBuilder.BuildLexicalRank(
            "articles", ArticleSchema(), "Body", ["fox"],
            hardFilter: null, SearchLogic.And, depth: 50, authz: null);

        sql.Should().Contain("SELECT s.__key");
        sql.Should().Contain("ORDER BY (");
        sql.Should().Contain("DESC, s.__key ASC");
        sql.Should().Contain("LIMIT 50");
    }

    [Fact]
    public void BuildLexicalRank_HardFilterAndOwnerAndTenantAreSeparateAndGroups()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new AuthorizationConstraint(
                null, "OwnerId", "u1", TenantColumn: "TenantId", TenantValue: "t1")
        };
        var hardFilter = new List<SearchClause>
        {
            new() { Property = "Title", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "x" } }
        };

        var (sql, param) = StarRocksQueryBuilder.BuildLexicalRank(
            "articles", ArticleSchema(), "Body", ["fox"],
            hardFilter, SearchLogic.And, depth: 50, authz);

        sql.Should().Contain("`OwnerId` = @__ownerVal");
        sql.Should().Contain("`TenantId` = @__tenantVal");
        param.Get<string>("__ownerVal").Should().Be("u1");
        param.Get<string>("__tenantVal").Should().Be("t1");
    }

    // ── BuildRankQuery ───────────────────────────────────────────────────────

    [Fact]
    public void BuildRankQuery_UnresolvableClauseProperty_ThrowsTranslationException()
    {
        var rankQuery = new SearchQuery();
        rankQuery.Clauses.Add(new SearchClause
        {
            Property = "NoSuchColumn", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "x" }
        });
        rankQuery.Sort.Add(new SearchSort { Property = "Title" });

        var act = () => StarRocksQueryBuilder.BuildRankQuery(
            "articles", ArticleSchema(), rankQuery, hardFilter: null, SearchLogic.And, depth: 50, authz: null);

        act.Should().Throw<EngagementQueryTranslationException>().WithMessage("*NoSuchColumn*");
    }

    [Fact]
    public void BuildRankQuery_UnresolvableSortProperty_ThrowsTranslationException()
    {
        var rankQuery = new SearchQuery();
        rankQuery.Sort.Add(new SearchSort { Property = "NoSuchColumn" });

        var act = () => StarRocksQueryBuilder.BuildRankQuery(
            "articles", ArticleSchema(), rankQuery, hardFilter: null, SearchLogic.And, depth: 50, authz: null);

        act.Should().Throw<EngagementQueryTranslationException>().WithMessage("*NoSuchColumn*");
    }

    [Fact]
    public void BuildRankQuery_UnauthorizedSortProperty_ThrowsTranslationException()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new AuthorizationConstraint(new HashSet<string> { "Id", "Title" }, null, null)
        };
        var rankQuery = new SearchQuery();
        rankQuery.Sort.Add(new SearchSort { Property = "Body" });

        var act = () => StarRocksQueryBuilder.BuildRankQuery(
            "articles", ArticleSchema(), rankQuery, hardFilter: null, SearchLogic.And, depth: 50, authz);

        act.Should().Throw<EngagementQueryTranslationException>().WithMessage("*not authorized*");
    }

    [Fact]
    public void BuildRankQuery_RankClausesAndHardFilterAreSeparateAndGroups_WithKeyTieBreaker()
    {
        var rankQuery = new SearchQuery();
        rankQuery.Clauses.Add(new SearchClause
        {
            Property = "Rating", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 3 }
        });
        rankQuery.Sort.Add(new SearchSort { Property = "PublishedAt", Descending = true });
        var hardFilter = new List<SearchClause>
        {
            new() { Property = "Name", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "x" } }
        };

        var (sql, _) = StarRocksQueryBuilder.BuildRankQuery(
            "authors", AuthorSchema(), rankQuery, hardFilter, SearchLogic.And, depth: 200, authz: null);

        sql.Should().Contain(") AND (");
        sql.Should().Contain("`PublishedAt` DESC, `Id` ASC");
        sql.Should().Contain("LIMIT 200");
    }
```

- [ ] **Step 2: Run — confirm the tests fail to compile (the methods don't exist yet)**

```bash
dotnet test Iverson.StarRocks.Tests --filter "FullyQualifiedName~BuildLexicalRank|FullyQualifiedName~BuildRankQuery"
```

- [ ] **Step 3: Implement `BuildLexicalRank`, `BuildRankQuery`, and their shared helpers**

In `StarRocksQueryBuilder.cs`, add after `BuildOrder` (after the closing brace at line 952, before `BuildRangeSql`):

```csharp
    /// <summary>
    /// Appends the owner and tenant predicates for <paramref name="typeName"/> from
    /// <paramref name="authz"/> onto <paramref name="where"/>, each as its own parenthesised AND
    /// group — mirroring BuildSearch's own no-join-branch appends. Written once here because
    /// BuildLexicalRank and BuildRankQuery below both need it and neither has a join branch.
    /// </summary>
    private static string AppendOwnerAndTenant(
        string typeName, IReadOnlyDictionary<string, AuthorizationConstraint>? authz,
        DynamicParameters param, string where)
    {
        if (authz is null || !authz.TryGetValue(typeName, out var constraint))
            return where;

        if (constraint.OwnerColumn is not null)
        {
            param.Add("__ownerVal", constraint.OwnerValue);
            var predicate = $"`{constraint.OwnerColumn}` = @__ownerVal";
            where = where.Length > 0 ? $"({where}) AND {predicate}" : predicate;
        }
        if (constraint.TenantColumn is not null)
        {
            param.Add("__tenantVal", constraint.TenantValue);
            var predicate = $"`{constraint.TenantColumn}` = @__tenantVal";
            where = where.Length > 0 ? $"({where}) AND {predicate}" : predicate;
        }
        return where;
    }

    /// <summary>
    /// Builds an ORDER BY clause for rank_query, exactly like BuildOrder except it THROWS on an
    /// unresolvable sort property instead of silently skipping it (design spec §3.1: a typo must
    /// not become a dropped sort key and arbitrary row order). Not a BuildOrder overload — the
    /// existing Search RPC's silent-skip behaviour must not change.
    /// </summary>
    private static string BuildStrictOrder(
        EngagementQuerySchema schema, IEnumerable<SearchSort> sorts,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz)
    {
        var parts = new List<string>();
        foreach (var s in sorts)
        {
            var resolved = ResolveColumn(schema, s.Property)
                ?? throw new EngagementQueryTranslationException(
                    $"rank_query sort property '{s.Property}' does not resolve to a column on '{schema.TypeName}'.");
            if (!IsFieldAllowed(resolved, schema, null, authz, out var typeName))
                throw new EngagementQueryTranslationException(
                    $"rank_query sort property '{s.Property}' on '{typeName}' is not authorized for this caller.");
            parts.Add($"`{resolved}` {(s.Descending ? "DESC" : "ASC")}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Design spec §4.1: hand-rolled BM25 (k1=1.2, b=0.75) over <paramref name="columnName"/>,
    /// since StarRocks 4.1.1 has no bm25()/score()/match_score(). <paramref name="tokens"/> must
    /// be non-empty (callers skip the call entirely when the tokenizer yields nothing — an empty
    /// token list would produce a "WHERE > 0" syntax error, not an empty result). Corpus
    /// statistics (N, avgdl, df) are computed over the SAME row set the ranking uses: tenant,
    /// ownership, and the request's hard filter.
    /// </summary>
    internal static (string Sql, DynamicParameters Param) BuildLexicalRank(
        string tableName,
        EngagementQuerySchema schema,
        string columnName,
        IReadOnlyList<string> tokens,
        IReadOnlyList<SearchClause>? hardFilter,
        SearchLogic hardFilterLogic,
        int depth,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz,
        string? tenantDatabase = null)
    {
        var param     = new DynamicParameters();
        var quotedCol = $"`{columnName}`";
        var quotedKey = $"`{schema.KeyColumnName}`";

        var tfSelects  = new List<string>();
        var dfSelects  = new List<string>();
        var scoreTerms = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var pName = $"__tok{i}";
            param.Add(pName, $@"\b{tokens[i]}\b");
            tfSelects.Add($"regexp_count(lower({quotedCol}), @{pName}) AS __tf{i}");
            dfSelects.Add($"sum(if(__tf{i}>0,1,0)) AS __df{i}");
            scoreTerms.Add(
                $"ln(1+(g.__n-g.__df{i}+0.5)/(g.__df{i}+0.5)) * s.__tf{i}*2.2/(s.__tf{i}+1.2*(0.25+0.75*s.__dl/g.__avgdl))");
        }
        var tfSum = string.Join("+", Enumerable.Range(0, tokens.Count).Select(i => $"s.__tf{i}"));

        // hard filter uses the LENIENT (schema-only) BuildWhere overload deliberately: filter
        // properties are already resolved/authorized once by the caller's own existing filter
        // validation before a hybrid request ever reaches here (design spec §3.2), unlike
        // rank_query's own clauses in BuildRankQuery below, which are new caller-facing surface.
        var where = BuildWhere(schema, hardFilter, hardFilterLogic, param, out _, null, authz);
        where = AppendOwnerAndTenant(schema.TypeName, authz, param, where);

        var sql = $"""
            WITH s AS (
                SELECT {quotedKey} AS __key, regexp_count(lower({quotedCol}), '\w+') AS __dl,
                       {string.Join(", ", tfSelects)}
                FROM {TenantIdentifier.Qualify(tenantDatabase, tableName)}
                {(where.Length > 0 ? $"WHERE {where}" : "")}
            ), g AS (
                SELECT count(*) AS __n, avg(__dl) AS __avgdl, {string.Join(", ", dfSelects)}
                FROM s
            )
            SELECT s.__key
            FROM s CROSS JOIN g
            WHERE {tfSum} > 0
            ORDER BY ({string.Join(" + ", scoreTerms)}) DESC, s.__key ASC
            LIMIT {depth}
            """;

        return (sql, param);
    }

    /// <summary>
    /// Design spec §4.2: rank_query as a soft boost. WHERE is (rank clauses) AND (hard filter)
    /// AND owner AND tenant — two independently-built groups, since SearchQuery carries its own
    /// AND/OR logic and cannot nest. Rank clause/sort resolution is STRICT (throws on an
    /// unresolvable property) via a local resolver, not BuildWhere/BuildOrder's own lenient
    /// resolution — see BuildStrictOrder's doc comment for why this can't reuse those directly.
    /// </summary>
    internal static (string Sql, DynamicParameters Param) BuildRankQuery(
        string tableName,
        EngagementQuerySchema schema,
        SearchQuery rankQuery,
        IReadOnlyList<SearchClause>? hardFilter,
        SearchLogic hardFilterLogic,
        int depth,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz,
        string? tenantDatabase = null)
    {
        var param = new DynamicParameters();

        string? StrictResolve(string p)
        {
            var resolved = ResolveColumn(schema, p)
                ?? throw new EngagementQueryTranslationException(
                    $"rank_query clause property '{p}' does not resolve to a column on '{schema.TypeName}'.");
            if (!IsFieldAllowed(resolved, schema, null, authz, out var typeName))
                throw new EngagementQueryTranslationException(
                    $"rank_query clause property '{p}' on '{typeName}' is not authorized for this caller.");
            return $"`{resolved}`";
        }

        var rankWhere = BuildWhere(StrictResolve, rankQuery.Clauses, rankQuery.Logic, param, "rq", out _);
        var hardWhere = BuildWhere(schema, hardFilter, hardFilterLogic, param, out _, null, authz);

        var where = rankWhere.Length > 0 && hardWhere.Length > 0 ? $"({rankWhere}) AND ({hardWhere})"
            : rankWhere.Length > 0 ? rankWhere
            : hardWhere;
        where = AppendOwnerAndTenant(schema.TypeName, authz, param, where);

        var quotedKey = $"`{schema.KeyColumnName}`";
        var order     = BuildStrictOrder(schema, rankQuery.Sort, authz);
        order = order.Length > 0 ? $"{order}, {quotedKey} ASC" : $"{quotedKey} ASC";

        var sql = $"SELECT {quotedKey} FROM {TenantIdentifier.Qualify(tenantDatabase, tableName)}" +
                  $"{(where.Length > 0 ? $" WHERE {where}" : "")} ORDER BY {order} LIMIT {depth}";

        return (sql, param);
    }
```

- [ ] **Step 4: Run — confirm green**

```bash
dotnet test Iverson.StarRocks.Tests --filter "FullyQualifiedName~BuildLexicalRank|FullyQualifiedName~BuildRankQuery"
```

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.StarRocks/StarRocksQueryBuilder.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksQueryBuilderTests.cs
git commit -m "add BuildLexicalRank and BuildRankQuery to StarRocksQueryBuilder

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 3: StarRocks interface, repository, and disabled-store stubs

**Files:**
- Modify: `Iverson.StarRocks/IEngagementStoreRoles.cs`
- Modify: `Iverson.StarRocks/EngagementRepository.cs`
- Modify: `Iverson.StarRocks/DisabledEngagementStoreSearchService.cs`
- Create: `Iverson.StarRocks.Tests/HybridRankIntegrationTests.cs`

**Interfaces:**
- Produces: `IEngagementStoreSearchService.SearchLexicalAsync(...)`, `.SearchRankQueryAsync(...)` — both `Task<IReadOnlyList<string>>`, ordered StarRocks keys. Task 5 consumes both.
- Consumes: Task 2's `BuildLexicalRank`/`BuildRankQuery`.

- [ ] **Step 1: Interface**

In `IEngagementStoreRoles.cs`, add to `IEngagementStoreSearchService` (after `PipelineAsync`):

```csharp

    /// <summary>Design spec §4: entity keys ranked by hand-rolled BM25 over <paramref
    /// name="columnName"/>, best-first. Empty when <paramref name="tokens"/> is empty (no error)
    /// or the tenant has no role/table yet.</summary>
    Task<IReadOnlyList<string>> SearchLexicalAsync(
        EngagementQuerySchema schema,
        string columnName,
        IReadOnlyList<string> tokens,
        IReadOnlyList<SearchClause>? hardFilter,
        SearchLogic hardFilterLogic,
        int depth,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null);

    /// <summary>Design spec §4: entity keys matching <paramref name="rankQuery"/>'s clauses, in
    /// its sort order (a soft boost, not a filter). Empty when the tenant has no role/table
    /// yet.</summary>
    Task<IReadOnlyList<string>> SearchRankQueryAsync(
        EngagementQuerySchema schema,
        SearchQuery rankQuery,
        IReadOnlyList<SearchClause>? hardFilter,
        SearchLogic hardFilterLogic,
        int depth,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null);
```

- [ ] **Step 2: Repository implementation**

In `EngagementRepository.cs`, add after `SearchAsync` (after its closing brace, before `AggregateAsync`):

```csharp

    public async Task<IReadOnlyList<string>> SearchLexicalAsync(
        EngagementQuerySchema schema,
        string columnName,
        IReadOnlyList<string> tokens,
        IReadOnlyList<SearchClause>? hardFilter,
        SearchLogic hardFilterLogic,
        int depth,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        if (tokens.Count == 0) return [];

        if (authz is null)
        {
            var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildLexicalRank(
                schema.TableName, schema, columnName, tokens, hardFilter, hardFilterLogic, depth, authz);
            return (await QueryAsync<string>(unscopedSql, unscopedParam)).ToList();
        }

        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return [];

        var (sql, param) = StarRocksQueryBuilder.BuildLexicalRank(
            schema.TableName, schema, columnName, tokens, hardFilter, hardFilterLogic, depth, authz,
            TenantIdentifier.DatabaseName(tenantId));

        try
        {
            return (await RunTenantScopedAsync(
                "sr.lexicalRank", tenantId, sql, conn => conn.QueryAsync<string>(sql, param),
                IsExpectedMissingResourceError)).ToList();
        }
        catch (Exception ex) when (IsExpectedMissingResourceError(ex))
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> SearchRankQueryAsync(
        EngagementQuerySchema schema,
        SearchQuery rankQuery,
        IReadOnlyList<SearchClause>? hardFilter,
        SearchLogic hardFilterLogic,
        int depth,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        if (authz is null)
        {
            var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildRankQuery(
                schema.TableName, schema, rankQuery, hardFilter, hardFilterLogic, depth, authz);
            return (await QueryAsync<string>(unscopedSql, unscopedParam)).ToList();
        }

        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return [];

        var (sql, param) = StarRocksQueryBuilder.BuildRankQuery(
            schema.TableName, schema, rankQuery, hardFilter, hardFilterLogic, depth, authz,
            TenantIdentifier.DatabaseName(tenantId));

        try
        {
            return (await RunTenantScopedAsync(
                "sr.rankQuery", tenantId, sql, conn => conn.QueryAsync<string>(sql, param),
                IsExpectedMissingResourceError)).ToList();
        }
        catch (Exception ex) when (IsExpectedMissingResourceError(ex))
        {
            return [];
        }
    }
```

- [ ] **Step 3: Disabled-store stubs**

In `DisabledEngagementStoreSearchService.cs`, add after `PipelineAsync`:

```csharp

    public Task<IReadOnlyList<string>> SearchLexicalAsync(
        EngagementQuerySchema schema, string columnName, IReadOnlyList<string> tokens,
        IReadOnlyList<SearchClause>? hardFilter, SearchLogic hardFilterLogic, int depth,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);

    public Task<IReadOnlyList<string>> SearchRankQueryAsync(
        EngagementQuerySchema schema, SearchQuery rankQuery,
        IReadOnlyList<SearchClause>? hardFilter, SearchLogic hardFilterLogic, int depth,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);
```

- [ ] **Step 4: Container integration tests**

Create `Iverson.StarRocks.Tests/HybridRankIntegrationTests.cs`:

```csharp
using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.StarRocks.Tests;

[Trait("Category", "Integration")]
[Collection(StarRocksCollection.Name)]
public sealed class HybridRankIntegrationTests
{
    private readonly StarRocksContainerFixture _fx;

    public HybridRankIntegrationTests(StarRocksContainerFixture fx) => _fx = fx;

    private static EngagementQuerySchema DocSchema() =>
        new("HybridDoc", "hybrid_docs", "Id", ["Body", "OwnerId", "TenantId"]);

    private async Task SeedAsync()
    {
        await _fx.Repository.ExecuteAsync("DROP TABLE IF EXISTS hybrid_docs");
        await _fx.Repository.ExecuteAsync("""
            CREATE TABLE hybrid_docs (
                Id VARCHAR(36) NOT NULL, Body VARCHAR(1048576), OwnerId VARCHAR(36), TenantId VARCHAR(36)
            ) ENGINE=OLAP
            PRIMARY KEY (Id) DISTRIBUTED BY HASH(Id) BUCKETS 4
            PROPERTIES ("replication_num" = "1")
            """);
        await _fx.Repository.ExecuteAsync("""
            INSERT INTO hybrid_docs VALUES
            ('a','the quick brown fox jumps over the lazy dog','u1','t1'),
            ('b','fox fox fox fox everywhere fox','u1','t1'),
            ('c','a slow brown dog sleeps','u1','t1'),
            ('d','nothing relevant here at all','u1','t1')
            """);
    }

    [Fact]
    public async Task SearchLexicalAsync_RanksRareTermDocumentsFirst()
    {
        await SeedAsync();

        var ranked = await _fx.Repository.SearchLexicalAsync(
            DocSchema(), "Body", ["dog"], hardFilter: null, SearchLogic.And, depth: 50);

        // "dog" appears in a and c only (df=2 of 4) — d and b must not appear.
        ranked.Should().BeSubsetOf(["a", "c"]);
        ranked.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchLexicalAsync_RespectsOwnerAndTenant()
    {
        await SeedAsync();
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["HybridDoc"] = new AuthorizationConstraint(null, "OwnerId", "u1", "TenantId", "t1")
        };

        var ranked = await _fx.Repository.SearchLexicalAsync(
            DocSchema(), "Body", ["fox"], hardFilter: null, SearchLogic.And, depth: 50, authz);

        ranked.Should().Contain("a").And.Contain("b");
    }

    [Fact]
    public async Task SearchLexicalAsync_MissingTable_ReturnsEmpty()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["NeverWritten"] = new AuthorizationConstraint(null, null, null, "TenantId", "no-such-tenant")
        };
        var schema = new EngagementQuerySchema("NeverWritten", "never_written", "Id", ["Body"]);

        var ranked = await _fx.Repository.SearchLexicalAsync(
            schema, "Body", ["fox"], hardFilter: null, SearchLogic.And, depth: 50, authz);

        ranked.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchRankQueryAsync_OrdersBySortAndAppliesHardFilter()
    {
        await SeedAsync();
        var rankQuery = new SearchQuery();
        rankQuery.Sort.Add(new SearchSort { Property = "Id", Descending = true });
        var hardFilter = new List<SearchClause>
        {
            new() { Property = "OwnerId", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "u1" } }
        };

        var ranked = await _fx.Repository.SearchRankQueryAsync(
            DocSchema(), rankQuery, hardFilter, SearchLogic.And, depth: 50);

        ranked.Should().Equal(["d", "c", "b", "a"]);
    }
}
```

- [ ] **Step 5: Run**

```bash
dotnet build Iverson.StarRocks/Iverson.StarRocks.csproj
dotnet test Iverson.StarRocks.Tests --filter "FullyQualifiedName~HybridRankIntegrationTests"
```

- [ ] **Step 6: Commit**

```bash
git add Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs Iverson.Server/Iverson.StarRocks/EngagementRepository.cs Iverson.Server/Iverson.StarRocks/DisabledEngagementStoreSearchService.cs Iverson.Server/Iverson.StarRocks.Tests/HybridRankIntegrationTests.cs
git commit -m "add SearchLexicalAsync and SearchRankQueryAsync to IEngagementStoreSearchService

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 4: Fusion primitives

**Files:**
- Create: `Iverson.Vector/ReciprocalRankFusion.cs`
- Create: `Iverson.Vector/HybridTokenizer.cs`
- Create: `Iverson.Vector.Tests/ReciprocalRankFusionTests.cs`
- Create: `Iverson.Vector.Tests/HybridTokenizerTests.cs`

**Interfaces:**
- Produces: `ReciprocalRankFusion.Fuse(...)`, `.RescaleOnto(...)`, `HybridTokenizer.Tokenize(...)`. Tasks 5-7 consume all three.

- [ ] **Step 1: `HybridTokenizer`**

Create `Iverson.Vector/HybridTokenizer.cs`:

```csharp
using System.Text;

namespace Iverson.Vector;

/// <summary>
/// The query-side half of the lexical-ranking tokenizer contract with
/// StarRocksQueryBuilder.BuildLexicalRank's document-side SQL: lowercase, then maximal runs of
/// ASCII [a-z0-9_], de-duplicated. ASCII-only because StarRocks 4.1.1's \w and \b are ASCII-only
/// while lower() is Unicode-aware — an ASCII token class is the only rule under which query
/// tokens and document tokens agree (design spec §4.1). Non-English text is a known limitation.
/// </summary>
public static class HybridTokenizer
{
    public static IReadOnlyList<string> Tokenize(string query)
    {
        var lowered = query.ToLowerInvariant();
        var tokens  = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var c in lowered)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                current.Append(c);
            else
                Flush();
        }
        Flush();

        return tokens.Distinct(StringComparer.Ordinal).ToList();
    }
}
```

- [ ] **Step 2: `ReciprocalRankFusion`**

Create `Iverson.Vector/ReciprocalRankFusion.cs`:

```csharp
namespace Iverson.Vector;

/// <summary>
/// Fuses the vector reranker's output with zero, one, or two StarRocks-derived rankings by
/// Reciprocal Rank Fusion: rrf(d) = sum over lists of 1/(K + rank), 1-based ranks. Pure and
/// I/O-free. A list not containing an id contributes nothing to its score.
/// <para>
/// Each extra list is an id -&gt; rank DICTIONARY rather than an ordered id list, so a caller can
/// assign the SAME rank to several ids without violating "rank = position" the way an ordered
/// list would. SearchChunks needs exactly this: every chunk of one parent document inherits that
/// parent's single StarRocks rank (design spec §5.1/§5.3), which an ordered list can't represent
/// without a fabricated per-chunk tie-break. For SearchSimilar's own two lists (entity-keyed, one
/// row per rank), the caller builds the dictionary from the StarRocks result's own 1-based
/// position instead.
/// </para>
/// </summary>
public static class ReciprocalRankFusion
{
    public const int K = 60;

    /// <param name="vectorRanked">The vector pipeline's own fused-descending output. Doubles as
    /// (a) the candidate set's base membership — an id present ONLY in an extra list is still
    /// included in the result (a StarRocks-only hit) — and (b) the first tie-break key via its
    /// own position.</param>
    /// <param name="lexicalRanks">Id -&gt; 1-based rank from the lexical StarRocks list, or null
    /// when that leg was not run.</param>
    /// <param name="rankQueryRanks">Id -&gt; 1-based rank from the rank_query StarRocks list, or
    /// null when that leg was not run.</param>
    public static IReadOnlyList<RerankedResult> Fuse(
        IReadOnlyList<RerankedResult> vectorRanked,
        IReadOnlyDictionary<ulong, int>? lexicalRanks,
        IReadOnlyDictionary<ulong, int>? rankQueryRanks)
    {
        var vectorRank = new Dictionary<ulong, int>(vectorRanked.Count);
        for (var i = 0; i < vectorRanked.Count; i++)
            vectorRank.TryAdd(vectorRanked[i].Id, i + 1);

        var ids = new HashSet<ulong>(vectorRank.Keys);
        if (lexicalRanks is not null) ids.UnionWith(lexicalRanks.Keys);
        if (rankQueryRanks is not null) ids.UnionWith(rankQueryRanks.Keys);

        double Score(ulong id)
        {
            var s = 0.0;
            if (vectorRank.TryGetValue(id, out var vr)) s += 1.0 / (K + vr);
            if (lexicalRanks is not null && lexicalRanks.TryGetValue(id, out var lr)) s += 1.0 / (K + lr);
            if (rankQueryRanks is not null && rankQueryRanks.TryGetValue(id, out var rr)) s += 1.0 / (K + rr);
            return s;
        }

        // Absent sorts after any present rank: int.MaxValue as the tie-break key for "no rank".
        static int TieRank(IReadOnlyDictionary<ulong, int>? ranks, ulong id) =>
            ranks is not null && ranks.TryGetValue(id, out var r) ? r : int.MaxValue;

        return ids
            .Select(id => (Id: id, Score: Score(id)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => TieRank(vectorRank, x.Id))
            .ThenBy(x => TieRank(lexicalRanks, x.Id))
            .ThenBy(x => TieRank(rankQueryRanks, x.Id))
            .ThenBy(x => x.Id)
            .Select(x => new RerankedResult(x.Id, x.Score))
            .ToList();
    }

    /// <summary>
    /// Linearly rescales <paramref name="fused"/>'s RRF scores onto <paramref
    /// name="vectorRanked"/>'s own [min,max] fused-score range, preserving <paramref
    /// name="fused"/>'s existing order (never re-sorts). RRF scores are roughly two orders of
    /// magnitude smaller than the fused scores MMR's penalty term is calibrated against (design
    /// spec §5.2), so an un-rescaled RRF score would swamp MMR's relevance term at any lambda
    /// &lt; 1.
    /// <list type="bullet">
    /// <item><description>If <paramref name="fused"/>'s own RRF scores are all equal (including
    /// the single-candidate case), every entry maps to <paramref name="vectorRanked"/>'s max —
    /// there is no source spread to map proportionally.</description></item>
    /// <item><description>If <paramref name="vectorRanked"/> is empty, there is no target range
    /// to rescale onto (this happens when Qdrant returned nothing but a StarRocks list did) —
    /// <paramref name="fused"/> is returned UNRESCALED. Order is unaffected either way; only an
    /// operator-set lambda &lt; 1 would ever observe the magnitude, and there is no baseline-arm
    /// spread to match here since the baseline arm never reaches MMR in this scenario
    /// either.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<RerankedResult> RescaleOnto(
        IReadOnlyList<RerankedResult> fused, IReadOnlyList<RerankedResult> vectorRanked)
    {
        if (fused.Count == 0 || vectorRanked.Count == 0) return fused;

        var targetMax = vectorRanked[0].FusedScore;
        var targetMin = vectorRanked[0].FusedScore;
        foreach (var r in vectorRanked)
        {
            if (r.FusedScore > targetMax) targetMax = r.FusedScore;
            if (r.FusedScore < targetMin) targetMin = r.FusedScore;
        }

        var sourceMax = fused[0].FusedScore;
        var sourceMin = fused[0].FusedScore;
        foreach (var r in fused)
        {
            if (r.FusedScore > sourceMax) sourceMax = r.FusedScore;
            if (r.FusedScore < sourceMin) sourceMin = r.FusedScore;
        }

        if (sourceMax == sourceMin || targetMax == targetMin)
            return fused.Select(r => r with { FusedScore = targetMax }).ToList();

        return fused
            .Select(r => r with
            {
                FusedScore = targetMin +
                    (r.FusedScore - sourceMin) / (sourceMax - sourceMin) * (targetMax - targetMin)
            })
            .ToList();
    }
}
```

- [ ] **Step 3: Tests**

Create `Iverson.Vector.Tests/HybridTokenizerTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace Iverson.Vector.Tests;

public sealed class HybridTokenizerTests
{
    [Fact]
    public void Tokenize_LowercasesAndSplitsOnNonWordChars()
    {
        HybridTokenizer.Tokenize("Fox-Jumps, over.the LAZY_dog!")
            .Should().Equal("fox", "jumps", "over", "the", "lazy_dog");
    }

    [Fact]
    public void Tokenize_DeduplicatesTokens()
    {
        HybridTokenizer.Tokenize("fox Fox FOX").Should().Equal("fox");
    }

    [Fact]
    public void Tokenize_NonAsciiCharacterIsASeparator()
    {
        // café -> "caf" + "é" is dropped as non-ASCII; StarRocks' \w/\b are ASCII-only too.
        HybridTokenizer.Tokenize("café").Should().Equal("caf");
    }

    [Fact]
    public void Tokenize_NoTokens_ReturnsEmpty()
    {
        HybridTokenizer.Tokenize("... !!! ???").Should().BeEmpty();
    }
}
```

Create `Iverson.Vector.Tests/ReciprocalRankFusionTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace Iverson.Vector.Tests;

public sealed class ReciprocalRankFusionTests
{
    private static IReadOnlyList<RerankedResult> Vector(params ulong[] idsInOrder) =>
        idsInOrder.Select((id, i) => new RerankedResult(id, 1.0 - i * 0.01)).ToList();

    private static Dictionary<ulong, int> Ranks(params ulong[] idsInOrder)
    {
        var d = new Dictionary<ulong, int>();
        for (var i = 0; i < idsInOrder.Length; i++) d[idsInOrder[i]] = i + 1;
        return d;
    }

    [Fact]
    public void Fuse_NoExtraLists_PreservesVectorOrder()
    {
        var result = ReciprocalRankFusion.Fuse(Vector(1, 2, 3), null, null);
        result.Select(r => r.Id).Should().Equal(1UL, 2UL, 3UL);
    }

    [Fact]
    public void Fuse_AgreementBetweenListsPromotesTheAgreedCandidate()
    {
        // Vector ranks 2 above 1; lexical strongly favours 1. RRF should let 1 win.
        var result = ReciprocalRankFusion.Fuse(Vector(2, 1), Ranks(1), null);
        result[0].Id.Should().Be(1UL);
    }

    [Fact]
    public void Fuse_IdOnlyInExtraList_IsIncludedInTheResult()
    {
        var result = ReciprocalRankFusion.Fuse(Vector(1, 2), Ranks(99), null);
        result.Select(r => r.Id).Should().Contain(99UL);
    }

    [Fact]
    public void Fuse_TieBreaksByVectorRankThenLexicalThenRankQueryThenId()
    {
        // 10 and 11 both absent from every list except sharing the SAME rank_query rank (2),
        // and both absent from vector/lexical -> falls through to id ascending.
        var rankQuery = new Dictionary<ulong, int> { [10] = 2, [11] = 2 };
        var result = ReciprocalRankFusion.Fuse(Vector(), null, rankQuery);
        result.Select(r => r.Id).Should().Equal(10UL, 11UL);
    }

    [Fact]
    public void RescaleOnto_AllFusedScoresEqual_MapsEveryEntryToTargetMax()
    {
        var fused  = new List<RerankedResult> { new(1, 0.01), new(2, 0.01) };
        var vector = Vector(1, 2, 3);

        var result = ReciprocalRankFusion.RescaleOnto(fused, vector);

        result.Should().OnlyContain(r => r.FusedScore == vector[0].FusedScore);
    }

    [Fact]
    public void RescaleOnto_LinearlyMapsSourceRangeOntoTargetRange()
    {
        var fused  = new List<RerankedResult> { new(1, 0.02), new(2, 0.01), new(3, 0.00) };
        var vector = new List<RerankedResult> { new(1, 0.9), new(2, 0.8), new(3, 0.5) };

        var result = ReciprocalRankFusion.RescaleOnto(fused, vector);

        result[0].FusedScore.Should().BeApproximately(0.9, 1e-9); // source max -> target max
        result[2].FusedScore.Should().BeApproximately(0.5, 1e-9); // source min -> target min
        result.Select(r => r.Id).Should().Equal(1UL, 2UL, 3UL);   // order preserved, not re-sorted
    }

    [Fact]
    public void RescaleOnto_VectorRankedEmpty_ReturnsFusedUnrescaled()
    {
        var fused = new List<RerankedResult> { new(1, 0.016), new(2, 0.008) };

        var result = ReciprocalRankFusion.RescaleOnto(fused, []);

        result.Should().Equal(fused);
    }
}
```

- [ ] **Step 4: Run**

```bash
dotnet test Iverson.Vector.Tests --filter "FullyQualifiedName~HybridTokenizer|FullyQualifiedName~ReciprocalRankFusion"
```

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Vector/ReciprocalRankFusion.cs Iverson.Server/Iverson.Vector/HybridTokenizer.cs Iverson.Server/Iverson.Vector.Tests/ReciprocalRankFusionTests.cs Iverson.Server/Iverson.Vector.Tests/HybridTokenizerTests.cs
git commit -m "add ReciprocalRankFusion and HybridTokenizer to Iverson.Vector

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 5: gRPC — validation and the shared StarRocks-fetch helper

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectSearchGrpcService.cs`

**Interfaces:**
- Consumes: Task 1's `HybridRank`, Task 3's `SearchLexicalAsync`/`SearchRankQueryAsync`, Task 4's `ReciprocalRankFusion`/`HybridTokenizer`.
- Produces: `ValidateHybridRequest`, `BuildStarRocksAuthz`, `HybridRanks`, `FetchHybridRanksAsync`. Tasks 6 and 7 consume all four.

- [ ] **Step 1: Add the validation and fetch helpers**

In `ObjectSearchGrpcService.cs`, add after `ValidateFilterProperty` (after its closing brace, currently ending at line 1105):

```csharp

    /// <summary>
    /// Design spec §3.1 validation for an optional HybridRank field, shared by SearchSimilar and
    /// SearchChunks. Runs before the query is embedded or StarRocks is touched. Two §3.1 rows are
    /// deliberately NOT here: "clause/sort property does not resolve" and "not in AllowedFields"
    /// both throw EngagementQueryTranslationException out of
    /// StarRocksQueryBuilder.BuildRankQuery itself (mapped in FetchHybridRanksAsync below,
    /// alongside Search's own use of the same exception type) — duplicating that check here would
    /// re-implement ResolveColumn/IsFieldAllowed a second time for no benefit.
    /// </summary>
    private static void ValidateHybridRequest(HybridRank? hybrid, IReadOnlyList<SearchClause> filter, string rpcName)
    {
        if (hybrid is null) return;

        if (!hybrid.Lexical && hybrid.RankQuery is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{rpcName}: hybrid is set but has neither lexical nor rank_query — nothing to fuse."));

        if (hybrid.RankQuery is not null && hybrid.RankQuery.Sort.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{rpcName}: hybrid.rank_query has no sort — StarRocks row order would be arbitrary."));

        // Design spec §3.2: Qdrant treats an empty IN list as Match(property, []); StarRocks'
        // BuildIn treats it (and any non-StringList IN value) as "no predicate" and DROPS the
        // clause — so a StarRocks list could admit rows the vector side excludes, with no
        // filtered Qdrant retrieve to cross-check against. Only reachable on SearchSimilar's head
        // path; SearchChunks' filter accepts EQUALS only.
        foreach (var clause in filter)
            if (clause.Operator == SearchOperator.In &&
                (clause.Value.KindCase != SearchValue.KindOneofCase.StringList ||
                 clause.Value.StringList.Values.Count == 0))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"{rpcName}: hybrid is set and filter property '{clause.Property}' has an empty IN " +
                    "list, which StarRocks and Qdrant treat differently."));
    }

    private static IReadOnlyDictionary<string, AuthorizationConstraint> BuildStarRocksAuthz(
        SchemaDescriptor schema, AuthorizationDecision decision) =>
        new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            [schema.TypeName] = new AuthorizationConstraint(
                decision.AllowedFields, decision.OwnerFieldName, decision.OwnerValue,
                decision.TenantColumn, decision.TenantValue)
        };

    private sealed record HybridRanks(
        IReadOnlyDictionary<ulong, int>? Lexical, IReadOnlyDictionary<ulong, int>? RankQuery);

    private static IReadOnlyDictionary<ulong, int> ToRankDictionary(IReadOnlyList<string> keys)
    {
        var ranks = new Dictionary<ulong, int>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
            ranks.TryAdd(IntelligenceStoreConsumer.KeyToUlong(keys[i]), i + 1);
        return ranks;
    }

    /// <summary>
    /// Runs the StarRocks leg(s) of an optional HybridRank field and returns each leg's id -&gt;
    /// 1-based-rank dictionary, entity-keyed (KeyToUlong — the same id space SearchSimilar's own
    /// byId/centroid lookups already use). Returns (null, null), calling neither StarRocks method,
    /// when hybrid is absent (design spec §8: "hybrid absent -&gt; the StarRocks methods are
    /// never called"). Maps every StarRocks failure per design spec §6 — including a raw,
    /// non-expected exception (e.g. a MySqlException escaping before the circuit trips), which
    /// none of Search/Aggregate/GroupBy/Pipeline's own (narrower) catch sets map, by design: a
    /// hybrid arm that silently lost its StarRocks leg would score as "StarRocks did nothing".
    /// </summary>
    private async Task<HybridRanks> FetchHybridRanksAsync(
        SchemaDescriptor schema, HybridRank? hybrid, string lexicalColumn, string query,
        IReadOnlyList<SearchClause> hardFilter, SearchLogic hardFilterLogic,
        IReadOnlyDictionary<string, AuthorizationConstraint> starRocksAuthz, ulong depth, string rpcName)
    {
        if (hybrid is null) return new HybridRanks(null, null);

        try
        {
            var srSchema = SchemaBuilder.ToEngagementQuerySchema(schema);
            var intDepth = (int)depth;

            IReadOnlyDictionary<ulong, int>? lexical = null;
            if (hybrid.Lexical)
            {
                var tokens = HybridTokenizer.Tokenize(query);
                var keys = await search.SearchLexicalAsync(
                    srSchema, lexicalColumn, tokens, hardFilter, hardFilterLogic, intDepth, starRocksAuthz);
                lexical = ToRankDictionary(keys);
            }

            IReadOnlyDictionary<ulong, int>? rankQuery = null;
            if (hybrid.RankQuery is not null)
            {
                var keys = await search.SearchRankQueryAsync(
                    srSchema, hybrid.RankQuery, hardFilter, hardFilterLogic, intDepth, starRocksAuthz);
                rankQuery = ToRankDictionary(keys);
            }

            return new HybridRanks(lexical, rankQuery);
        }
        catch (EngagementQueryTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (EngagementStoreDisabledException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (EngagementNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"StarRocks is not ready: {ex.Message}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
        {
            logger.LogError(ex, "[{Rpc}] hybrid StarRocks query failed", rpcName);
            throw new RpcException(new Status(StatusCode.Unavailable, "StarRocks unavailable for hybrid ranking."));
        }
    }
```

- [ ] **Step 2: Build (compile-only — nothing calls these yet)**

```bash
dotnet build Iverson.Api/Iverson.Api.csproj
```

- [ ] **Step 3: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs
git commit -m "add ValidateHybridRequest and FetchHybridRanksAsync to ObjectSearchGrpcService

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 6: gRPC — SearchSimilar wiring (head path + chunks-routed path)

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectSearchGrpcService.cs`

**Interfaces:**
- Consumes: Task 5's helpers, Task 4's `ReciprocalRankFusion`.

- [ ] **Step 1: Validate up front**

In `SearchSimilar`, immediately after line 181 (`var topK = ResolveTopK(request.TopK, queryLimits.MaxTopK, "SearchSimilar");`), insert:

```csharp

        ValidateHybridRequest(request.Hybrid, request.Filter, "SearchSimilar");
```

- [ ] **Step 2: Head path — replace lines 305-330**

Replace (the block from `var candidates = results.Select(...)` through the `foreach (var ranked in diversifier.Diversify(...))` loop, i.e. current lines 305-330, keeping line 303's `var now = ...` and lines 332-334's `WriteSimilarResponsesAsync` call as they are) with:

```csharp
        var candidates = results.Select(r => new RerankCandidate(
            Id:         r.Id,
            BaseScore:  r.Score,
            Centroid:   centroids.TryGetValue(r.Id, out var centroid) ? centroid : null,
            Decay:      DecayFor(r, decayField, now, _decayOptions.HalfLifeDays),
            Popularity: PopularityFor(schema, r, _popularitySignal, now))).ToList();

        var byId         = ResultsById(results);
        var vectorRanked = reranker.Rerank(queryVector, candidates);

        var starRocksOnlyPayloads   = new Dictionary<ulong, IReadOnlyDictionary<string, string>>();
        var starRocksHydrationFailed = false;
        if (request.Hybrid is not null)
        {
            var starRocksDepth = (ulong)Math.Min(topK * OverFetchFactor, (ulong)queryLimits.MaxPageSize);
            var ranks = await FetchHybridRanksAsync(
                schema, request.Hybrid, vectorDesc.PropertyName, request.Query,
                request.Filter, request.FilterLogic, BuildStarRocksAuthz(schema, decision),
                starRocksDepth, "SearchSimilar");

            var fused = ReciprocalRankFusion.Fuse(vectorRanked, ranks.Lexical, ranks.RankQuery);
            vectorRanked = ReciprocalRankFusion.RescaleOnto(fused, vectorRanked);

            // A StarRocks list can rank an entity Qdrant's own search never returned. Design spec
            // §5.3: hydrate its payload from the object collection, and fetch its centroid
            // alongside everyone else's so an operator-set LambdaSimilar < 1 doesn't leave it
            // penalty-free. Degrades (skips just the StarRocks-only additions), unlike the
            // chunks-routed path's hydration below, because the Qdrant-native results in `byId`
            // are already complete and valid on their own.
            var starRocksOnlyIds = vectorRanked.Select(r => r.Id).Where(id => !byId.ContainsKey(id)).ToList();
            if (starRocksOnlyIds.Count > 0)
            {
                using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collectionName, readOnly: true)))
                {
                    try
                    {
                        foreach (var (id, payload) in await vector.RetrievePayloadAsync(collectionName, starRocksOnlyIds))
                            starRocksOnlyPayloads[id] = payload;
                    }
                    catch (RpcException ex)
                    {
                        starRocksHydrationFailed = true;
                        logger.LogWarning(ex,
                            "[SearchSimilar] StarRocks-only hydration failed (collection={Collection}); {Count} hit(s) skipped.",
                            collectionName.SanitizeForLog(), starRocksOnlyIds.Count);
                    }
                }

                if (centroidPossible)
                {
                    var extraCentroids = await RetrieveVectorsOrDegradeAsync(
                        collectionName, starRocksOnlyIds, vectorDesc.PropertyName.ToSnakeCase() + "_centroid",
                        "SearchSimilar", "re-ranking a StarRocks-only hit without the centroid signal");
                    centroids = centroids.Concat(extraCentroids).ToDictionary(kv => kv.Key, kv => kv.Value);
                }
            }
        }

        // A candidate whose diversity vector is ABSENT contributes no similarity term and so takes
        // no penalty, which means it outranks an otherwise-equal candidate that has a vector and
        // any positive similarity. Accepted by design: substituting a value would break the
        // bit-exact Take(topK) degradation guarantee. See the design spec's Known issues.
        var diversityCandidates = vectorRanked
            .Select(r => new DiversifyCandidate(
                r.Id,
                r.FusedScore,
                centroids.TryGetValue(r.Id, out var v) ? v : null))
            .ToList();

        var rows = new List<(IReadOnlyDictionary<string, string> Payload, double Score)>();
        foreach (var ranked in diversifier.Diversify(diversityCandidates, (int)topK, _ranking.LambdaSimilar))
        {
            if (byId.TryGetValue(ranked.Id, out var r))
                rows.Add((r.Payload, ranked.FusedScore));
            else if (starRocksOnlyPayloads.TryGetValue(ranked.Id, out var p))
                rows.Add((p, ranked.FusedScore));
            else if (!starRocksHydrationFailed)
                logger.LogWarning("[SearchSimilar] StarRocks-only hit {Id} missing at hydration; skipped", ranked.Id);
        }
```

- [ ] **Step 3: Chunks-routed path — replace lines 392-409**

Replace (the block from the `// Spec §3.5.1: max-passage collapse.` comment through `var selected = diversifier.Diversify(collapsed, (int)topK, _ranking.LambdaSimilar);`, i.e. current lines 392-409) with:

```csharp
        // Spec §3.5.1: max-passage collapse. Rerank output is fused-descending (ResultReranker.cs:44-45),
        // so the first sighting of a parent is its best chunk; ties keep first-seen order.
        var byId          = ResultsById(pipeline.Results);
        var seen          = new HashSet<ulong>();
        var docCentroids  = new Dictionary<ulong, float[]?>();
        var docRanked     = new List<RerankedResult>();
        foreach (var fused in pipeline.Fused)
        {
            if (!byId.TryGetValue(fused.Id, out var r)) continue;
            if (!r.Payload.TryGetValue("parent_id", out var parentKey) || string.IsNullOrEmpty(parentKey)) continue;
            var parentId = IntelligenceStoreConsumer.KeyToUlong(parentKey);
            if (!seen.Add(parentId)) continue;
            docRanked.Add(new RerankedResult(parentId, fused.FusedScore));
            docCentroids[parentId] = pipeline.Centroids.TryGetValue(parentId, out var centroid) ? centroid : null;
        }

        if (request.Hybrid is not null)
        {
            var starRocksDepth = (ulong)Math.Min(topK * OverFetchFactor, (ulong)queryLimits.MaxPageSize);
            var ranks = await FetchHybridRanksAsync(
                schema, request.Hybrid, chunkDesc.PropertyName, request.Query,
                request.Filter, request.FilterLogic, BuildStarRocksAuthz(schema, decision),
                starRocksDepth, "SearchSimilar");

            var fusedDocs = ReciprocalRankFusion.Fuse(docRanked, ranks.Lexical, ranks.RankQuery);
            docRanked = ReciprocalRankFusion.RescaleOnto(fusedDocs, docRanked).ToList();

            var starRocksOnlyDocIds = docRanked.Select(r => r.Id).Where(id => !docCentroids.ContainsKey(id)).ToList();
            if (starRocksOnlyDocIds.Count > 0)
            {
                var extraCentroids = await RetrieveVectorsOrDegradeAsync(
                    objectCollection, starRocksOnlyDocIds, chunkDesc.PropertyName.ToSnakeCase() + "_centroid",
                    "SearchSimilar", "re-ranking a StarRocks-only hit without the centroid signal");
                foreach (var id in starRocksOnlyDocIds)
                    docCentroids[id] = extraCentroids.TryGetValue(id, out var c) ? c : null;
            }
        }

        var collapsed = docRanked
            .Select(r => new DiversifyCandidate(r.Id, r.FusedScore, docCentroids.GetValueOrDefault(r.Id)))
            .ToList();

        // Spec §3.5.2: document-level diversification with LambdaSimilar on the centroid.
        var selected = diversifier.Diversify(collapsed, (int)topK, _ranking.LambdaSimilar);
```

(Everything from `// Spec §3.5.3: hydrate the top parents from the object collection.` onward is unchanged — it already batches `RetrievePayloadAsync` over every id in `selected`, which now includes any StarRocks-only documents too.)

- [ ] **Step 4: Run — full head-path/chunks-routed suite**

```bash
dotnet build Iverson.Api/Iverson.Api.csproj
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
```

Expect green: `request.Hybrid` is unset (default) on every existing test request, so `ValidateHybridRequest` returns immediately and the `if (request.Hybrid is not null)` blocks never execute — behaviour is unchanged.

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs
git commit -m "wire hybrid RRF fusion into SearchSimilar (head path and chunks-routed path)

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 7: gRPC — SearchChunks wiring, and hybrid-specific service tests

**Files:**
- Modify: `Iverson.Api/Grpc/ObjectSearchGrpcService.cs`
- Modify: `Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs`

**Interfaces:**
- Consumes: Task 5's helpers, Task 4's `ReciprocalRankFusion`.

- [ ] **Step 1: Validate up front**

In `SearchChunks`, immediately after line 524 (`var topK = ResolveTopK(request.TopK, queryLimits.MaxTopK, "SearchChunks");`), insert:

```csharp

        ValidateHybridRequest(request.Hybrid, request.Filter, "SearchChunks");
```

- [ ] **Step 2: Fuse before diversification — replace lines 595-606**

Replace (from `var byId = ResultsById(pipeline.Results);` through the `.ToList();` closing `diversityCandidates`, i.e. current lines 595-606) with:

```csharp
        var byId         = ResultsById(pipeline.Results);
        var vectorRanked = pipeline.Fused;

        if (request.Hybrid is not null)
        {
            var starRocksDepth = (ulong)Math.Min(topK * OverFetchFactor, (ulong)queryLimits.MaxPageSize);
            var ranks = await FetchHybridRanksAsync(
                schema, request.Hybrid, chunkDesc.PropertyName, request.Query,
                request.Filter, request.FilterLogic, BuildStarRocksAuthz(schema, decision),
                starRocksDepth, "SearchChunks");

            // Design spec §5.3: re-rank only. A chunk inherits its PARENT document's StarRocks
            // rank via the same parent_id payload centroids/popularity already key on — never its
            // own id, and never a document StarRocks found that has no chunk in the pool (that
            // document contributes no dictionary entry below, same as any other absent rank). This
            // is why the candidate SET here is always exactly pipeline.Fused's own ids: unlike
            // SearchSimilar, a StarRocks-only document can never be unioned in.
            IReadOnlyDictionary<ulong, int>? ChunkRanks(IReadOnlyDictionary<ulong, int>? parentRanks)
            {
                if (parentRanks is null) return null;
                var chunkRanks = new Dictionary<ulong, int>();
                foreach (var r in pipeline.Results)
                    if (r.Payload.TryGetValue("parent_id", out var parentKey) && !string.IsNullOrEmpty(parentKey) &&
                        parentRanks.TryGetValue(IntelligenceStoreConsumer.KeyToUlong(parentKey), out var rank))
                        chunkRanks[r.Id] = rank;
                return chunkRanks;
            }

            var fused = ReciprocalRankFusion.Fuse(vectorRanked, ChunkRanks(ranks.Lexical), ChunkRanks(ranks.RankQuery));
            vectorRanked = ReciprocalRankFusion.RescaleOnto(fused, vectorRanked);
        }

        var diversityCandidates = vectorRanked
            .Select(r => new DiversifyCandidate(
                r.Id,
                r.FusedScore,
                chunkVectors.TryGetValue(r.Id, out var v) ? v : null))
            .ToList();
```

(The `chunkVectors` fetch above this block, and the `foreach (var ranked in diversifier.Diversify(...))` streaming loop below it, are unchanged.)

- [ ] **Step 3: Hybrid-specific gRPC service tests**

In `ObjectSearchGrpcServiceTests.cs`, add a new region (near the end of the class, before its closing `}`), following exactly the file's own `MakeStream<T>()` / `SchemaFixtures.ArticleSchema()` / named-argument `VectorSearchResult` conventions used by the existing `SearchSimilar_CallsEmbedThenQdrant_AndStreamsResults` (Title, embedding-only) and `SearchChunks_...` (Body, chunk-only) tests immediately above this block:

```csharp

    // ── Hybrid RRF ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchSimilar_HybridSetWithNeitherLegRequested_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchSimilarRequest
        {
            TypeName = "Article", Property = "Title", Query = "q", TopK = 5,
            Hybrid = new HybridRank { Lexical = false }
        };

        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchSimilar_RankQueryWithNoSort_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var (writer, _) = MakeStream<SearchResponse>();
        var rankQuery = new SearchQuery();
        rankQuery.Clauses.Add(new SearchClause
        {
            Property = "Title", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "x" }
        });
        var request = new SearchSimilarRequest
        {
            TypeName = "Article", Property = "Title", Query = "q", TopK = 5,
            Hybrid = new HybridRank { RankQuery = rankQuery }
        };

        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchSimilar_HybridSetWithEmptyInFilter_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchSimilarRequest
        {
            TypeName = "Article", Property = "Title", Query = "q", TopK = 5,
            Hybrid = new HybridRank { Lexical = true }
        };
        request.Filter.Add(new SearchClause
        {
            Property = "Title", Operator = SearchOperator.In, Value = new SearchValue { StringList = new RepeatedString() }
        });

        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchSimilar_HybridAbsent_NeverCallsStarRocksSearchMethods()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var fakeVector = new float[768];
        _embedding.EmbedQueryAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync(Arg.Any<string>(), "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
            .Returns(new List<VectorSearchResult>().AsReadOnly());

        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "test query", TopK = 5 };
        await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        await _search.DidNotReceive().SearchLexicalAsync(
            Arg.Any<EngagementQuerySchema>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<SearchClause>?>(), Arg.Any<SearchLogic>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>());
        await _search.DidNotReceive().SearchRankQueryAsync(
            Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery>(),
            Arg.Any<IReadOnlyList<SearchClause>?>(), Arg.Any<SearchLogic>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>());
    }

    [Fact]
    public async Task SearchSimilar_HybridLexical_BoostedDocumentOutranksItsVectorOnlyPosition()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var fakeVector = new float[768];
        _embedding.EmbedQueryAsync("fox", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync(Arg.Any<string>(), "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
            .Returns(new List<VectorSearchResult>
            {
                new(Id: 1, Score: 0.9, Payload: new Dictionary<string, string> { ["title"] = "Vector Winner" }),
                new(Id: 2, Score: 0.8, Payload: new Dictionary<string, string> { ["title"] = "Lexical Winner" }),
            }.AsReadOnly());
        // Lexical strongly favours id 2's key (StarRocks keys are the entity's Guid string;
        // KeyToUlong maps this fixed Guid to 2 the same way EngagementRepository's real writes
        // would). Vector-loser, lexical-winner.
        _search.SearchLexicalAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<SearchClause>?>(), Arg.Any<SearchLogic>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((IReadOnlyList<string>)["00000000-0000-0000-0200-000000000000"]);

        var (writer, written) = MakeStream<SearchResponse>();
        var request = new SearchSimilarRequest
        {
            TypeName = "Article", Property = "Title", Query = "fox", TopK = 5, Hybrid = new HybridRank { Lexical = true }
        };
        await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        written[0].Data.Fields["Title"].StringValue.Should().Be("Lexical Winner");
    }

    [Fact]
    public async Task SearchChunks_HybridLexical_StarRocksOnlyDocumentContributesNoChunk()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var fakeVector = new float[768];
        _embedding.EmbedQueryAsync("fox", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync(Arg.Any<string>(), "body_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
            .Returns(new List<VectorSearchResult>
            {
                new(Id: 1, Score: 0.9,
                    Payload: new Dictionary<string, string>
                    {
                        ["text"] = "t1", ["parent_id"] = "00000000-0000-0000-0100-000000000000"
                    }),
            }.AsReadOnly());
        // StarRocks finds a SECOND document with no chunk in the pool — must not appear.
        _search.SearchLexicalAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<SearchClause>?>(), Arg.Any<SearchLogic>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((IReadOnlyList<string>)
            [
                "00000000-0000-0000-0100-000000000000",
                "00000000-0000-0000-6300-000000000000",
            ]);

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        var request = new SearchChunksRequest
        {
            TypeName = "Article", Property = "Body", Query = "fox", TopK = 5, Hybrid = new HybridRank { Lexical = true }
        };
        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].ParentKey.Should().Be("00000000-0000-0000-0100-000000000000");
    }

    [Fact]
    public async Task SearchSimilar_StarRocksUnavailable_ThrowsUnavailable()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var fakeVector = new float[768];
        _embedding.EmbedQueryAsync("fox", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync(Arg.Any<string>(), "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
            .Returns(new List<VectorSearchResult>().AsReadOnly());
        _search.SearchLexicalAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyList<SearchClause>?>(), Arg.Any<SearchLogic>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Throws(new MySqlConnector.MySqlException("connection reset"));

        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchSimilarRequest
        {
            TypeName = "Article", Property = "Title", Query = "fox", TopK = 5, Hybrid = new HybridRank { Lexical = true }
        };
        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }
```

The `00000000-0000-0000-NN00-000000000000` Guid strings are not arbitrary: `IntelligenceStoreConsumer.KeyToUlong` (`Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:650-658`) parses the Guid, takes `bytes[8..15]` of `Guid.ToByteArray()`, and reads them as a little-endian `ulong` — so the Guid's FOURTH group (the `NN00` above) becomes the ulong's low byte. Verified live (this session, `dotnet run` against exactly this function): `00000000-0000-0000-0100-000000000000` → `1`, `...-0200-...` → `2`, `...-6300-...` → `99`, matching the `VectorSearchResult.Id`/parent-id values the fake Qdrant results above use. (An earlier draft of this plan used `...-0000-0000000000NN`, which round-trips to a very different large number, not `NN` — corrected before verification.)

- [ ] **Step 4: Run**

```bash
dotnet build Iverson.Api/Iverson.Api.csproj
dotnet test Iverson.Api.Tests --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
```

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectSearchGrpcServiceTests.cs
git commit -m "wire hybrid RRF fusion into SearchChunks; add hybrid-specific gRPC service tests

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 8: Harness flag

**Files:**
- Modify: `Iverson.LoadTest/Program.cs`
- Modify: `Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs`

- [ ] **Step 1: Add the flag**

In `Program.cs`, add to `CommandFlags` (after `public double Beta { get; init; }`):

```csharp
    public bool   HybridLexical { get; init; }
```

And to `CommandFlags.Parse` (after `Beta = DblFlag(args, "--beta", 0),`):

```csharp
        HybridLexical = args.Contains("--hybrid-lexical"),
```

Add a help-text line near the existing `--chunk-budget-multiplier` line (286):

```
              --hybrid-lexical               benchmark-query only: sets hybrid.lexical=true on SearchSimilar/SearchChunks (experimental)
```

- [ ] **Step 2: Apply the flag and record it in the sidecar**

In `BenchmarkQueryScenario.cs`, add `flags.HybridLexical` to the sidecar `JsonObject` (after `["chunkBudgetMultiplier"] = flags.ChunkBudgetMultiplier,`):

```csharp
                ["hybridLexical"] = flags.HybridLexical,
```

In `RunSimilarAsync`, after building the request and before the `try`:

```csharp
        var request = Query.Similar<BenchmarkDocument>(d => d.Body)
            .Text(query.Text)
            .TopK(DocumentBudget)
            .Build();
        if (HybridLexical) request.Hybrid = new HybridRank { Lexical = true };
```

In `RunChunksAsync`, after building the request and before the `try`:

```csharp
        var request = Query.Chunks<BenchmarkDocument>(d => d.Body)
            .Text(query.Text)
            .TopK((uint)(DocumentBudget * chunkBudgetMultiplier))
            .Build();
        if (HybridLexical) request.Hybrid = new HybridRank { Lexical = true };
```

Since `RunSimilarAsync`/`RunChunksAsync` don't currently take `flags`, add a private field set from the constructor's existing `LoadTestConfig config` parameter... **no** — `HybridLexical` is a per-*run* flag (`CommandFlags`), not a per-*deployment* setting (`LoadTestConfig`). Thread it through explicitly instead: add a `bool hybridLexical` parameter to both `RunSimilarAsync` and `RunChunksAsync`, and pass `flags.HybridLexical` at their two call sites inside `RunAsync`'s query loop (`var similar = await RunSimilarAsync(query, headers, ct);` and `var chunks = await RunChunksAsync(query, headers, keyMap, reranker, rerankInput, corpusText, flags.ChunkBudgetMultiplier, ct);`), matching how `flags.ChunkBudgetMultiplier` is already threaded through to `RunChunksAsync` today.

- [ ] **Step 3: Document the run precondition**

Immediately above the `RunAsync` method's existing `var keyMap = await KeyMap.LoadAsync(...)` line, add:

```csharp
        // Design spec §7 run precondition (documented, not enforced): before a --hybrid-lexical
        // run, confirm the tenant's StarRocks row count for BenchmarkDocument equals the Qdrant
        // object-collection point count. The existing benchmark corpora were ingested through the
        // write path (which feeds both stores), but whether a given preserved volume's StarRocks
        // rows still match its Qdrant points has not been re-checked here. Iverson.LoadTest has no
        // Qdrant client today (only MySqlConnector, used by DirectSeeder) — adding one solely for
        // this check was judged out of scope for an experimental flag; verify by hand
        // (`SELECT COUNT(*) FROM <db>.benchmark_document` vs. the collection's point count) first.
```

- [ ] **Step 4: Build**

```bash
dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj
```

- [ ] **Step 5: Commit**

```bash
git add Iverson.Server/Iverson.LoadTest/Program.cs Iverson.Server/Iverson.LoadTest/Scenarios/BenchmarkQueryScenario.cs
git commit -m "add --hybrid-lexical flag to benchmark-query

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

## Known issues inherited from spec

- **Cross-store filter semantics are not proven identical beyond empty `IN`.** SQL `<>` excludes NULLs where Qdrant `must_not` includes points lacking the field, which makes StarRocks stricter (the safe direction). Timestamp comparison formats are unexamined.
- **The tokenizer is ASCII-only** (§4.1).
- **Lexical scoring is a full scan** of the tenant's table per query (about 0.4 s at 25K docs, roughly linear). Acceptable for an experiment; not a production retrieval path.
- **RRF k, BM25 k1 and BM25 b are uncalibrated constants.**
