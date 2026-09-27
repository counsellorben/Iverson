# MatchPattern Stores and RPC (Plan 2 of 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-09-17-row-pattern-matching-design.md` (commit SHA: `3fe355e1`)

**Goal:** Expose the merged `Iverson.Patterns` engine as the `ObjectSearchService.MatchPattern` streaming RPC over StarRocks rows (`TYPE_ROWS`) and Qdrant chunks (`CHUNKS`), with `SIMILARITY` scores, the §5 limits and the §6 failure mapping (spec §1 proto, §3.2–§3.4, §4–§8, §9.3–§9.4).

**Architecture:** The proto gains the RPC and its messages. `Iverson.StarRocks` gains `MatchRowsAsync`, which validates the request against `ColumnsFor` and streams tenant-scoped rows through a new streaming wrapper. `Iverson.Vector` gains `ScrollAsync` and a two-phase `IChunkRowSource`. `Iverson.Api` gains a `SimilarityResolver`, a partition batcher, `PatternQueryLimitOptions`, and the RPC itself, in a new partial-class file of `ObjectSearchGrpcService`. The RPC compiles the pattern with the engine, streams whole partitions in batches, resolves similarity per batch, runs `CompiledPattern.Run` per partition under one per-request `PatternBudget` that carries the request's timeout token, and writes each output row. Plan 1 built the engine and it is already on `main`. Plan 3 (the SDKs and conformance) consumes this RPC.

**Tech stack:** .NET 10, gRPC (`Grpc.AspNetCore` 2.83.0; the proto is compiled by `Iverson.Client.Contracts` with `Grpc.Tools` 2.81.1), Dapper 2.1.89 and MySqlConnector 2.4.0 against StarRocks 4.1.1, `Qdrant.Client` 1.18.1 against Qdrant 1.18.2, `System.Numerics.Tensors` 10.0.10, xunit 2.9.3, FluentAssertions 8.11.0, NSubstitute 5.3.0, Testcontainers 4.15.0.

---

## Global Constraints

- **No expression, pattern or subset string is ever spliced into SQL** (spec §2). SQL identifiers come only from `ColumnsFor` (spec §3.2).
- SQL `NULL` is C# `null` throughout. The TYPE_ROWS source converts `DBNull.Value` to `null` (spec §3.2).
- Row dictionaries from both sources compare keys with `StringComparer.OrdinalIgnoreCase`; output names stay ordinal (spec §3.2).
- **Validate before any store call:** the limits (§5), then `PatternQuery.Compile`. No store is touched until Compile succeeds (spec §3.4 step 2). `MaxPatternLength` and `MaxExpressionLength` are checked before `Compile` because the engine's parsers recurse once per nesting level (Plan 1's recorded obligation).
- **One timeout token per request.** Create it with `CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken)` plus `CancelAfter(TimeoutSeconds)`. Pass it to every StarRocks, Qdrant and embedding call, construct the single per-request `PatternBudget` with it, and check it before each output row (spec §3.2, §3.4 step 6). Any exception raised after it is cancelled maps to `DeadlineExceeded`, and that rule is checked before every other §6 row.
- **`SimilarityResolver` must not use `RetrieveVectorsOrDegradeAsync`** (spec §3.3).
- **A denied caller gets an empty stream** and no store is queried (spec §3.4 step 4, §6).
- Container-backed tests carry `[Trait("Category", "Integration")]` and join the assembly's existing collection: `StarRocksCollection.Name` in `Iverson.StarRocks.Tests`, and `ContainerCollection.Name` in `Iverson.Vector.Tests` and `Iverson.Api.Tests`. CI runs `dotnet test Iverson.slnx --filter "Category!=Integration"`.
- **Manual mutation check in every task review** (spec §9.6): delete or invert the named production branch, confirm that the covering test fails, restore it, and confirm `git status --porcelain` is clean.
- Commit messages are lowercase imperative sentences.

## File Structure

**Modify:**
- `Iverson.Clients/Common/Proto/object_search.proto` — add the `MatchPattern` RPC and its messages (Task 1).
- `Iverson.Server/Iverson.Api/Iverson.Api.csproj`, `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj` — add a project reference to `Iverson.Patterns` (Task 1).
- `Iverson.Server/Iverson.Api/Dockerfile` — add the `Iverson.Patterns.csproj` `COPY` line (Task 1).
- `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — `sealed` becomes `sealed partial` (Task 1); add the two optional constructor parameters (Task 7).
- `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs` — `ColumnsFor` goes from `private` to `internal` (Task 2).
- `Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs`, `EngagementRepository.cs`, `DisabledEngagementStoreSearchService.cs` — add `MatchRowsAsync` and the streaming wrapper (Task 3).
- `Iverson.Server/Iverson.Api.Tests/AdminConsoleDataVolumeEndpointTests.cs`, `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs` — the two hand-written test fakes of `IEngagementStoreSearchService` gain `MatchRowsAsync` (Task 3).
- `Iverson.Server/Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs` — the fixture exposes `PauseAsync`/`UnpauseAsync` (Task 3).
- `Iverson.Server/Iverson.Vector/IVectorRoles.cs`, `IntelligenceVectorService.cs` — add `ScrollAsync`; give `RetrieveNamedVectorAsync` an optional `CancellationToken` (Task 4).
- `Iverson.Server/Iverson.Api/Program.cs` — build and register `PatternQueryLimitOptions` (Task 7).
- `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs` — the `RemoveTenantColumn(IDictionary)` doc comment names `MatchPattern` as a caller (Task 7).

**Create:**
- `Iverson.Server/Iverson.StarRocks/MatchRowsRequest.cs`, `MatchRowsQueryBuilder.cs` (Task 2).
- `Iverson.Server/Iverson.Vector/VectorScrollPage.cs`, `IChunkRowSource.cs`, `QdrantChunkRowSource.cs` (Task 4).
- `Iverson.Server/Iverson.Api/Grpc/SimilarityResolver.cs` (Task 5).
- `Iverson.Server/Iverson.Api/Grpc/PatternPartitionBatcher.cs` (Task 6).
- `Iverson.Server/Iverson.Api/Grpc/PatternQueryLimitOptions.cs`, `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.MatchPattern.cs` (Task 7).

**Test (create):**
- `Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternProtoTests.cs` (Task 1).
- `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsQueryBuilderTests.cs` (Task 2).
- `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs` (Task 3).
- `Iverson.Server/Iverson.Vector.Tests/ChunkRowSourceIntegrationTests.cs` (Task 4).
- `Iverson.Server/Iverson.Api.Tests/Grpc/SimilarityResolverIntegrationTests.cs` (Task 5).
- `Iverson.Server/Iverson.Api.Tests/Grpc/PatternPartitionBatcherTests.cs` (Task 6).
- `Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternGrpcServiceTests.cs` (Task 7).
## Inherited from spec

The following assumptions were verified by `thorough-brainstorming` and eight `critical-design-review` rounds at spec-write time, and are NOT re-verified here. They are trusted as ground truth. This list includes only the rows Plans 2–3 rest on; the engine-only rows belong to Plan 1.

- One proto feeds the server and all five SDKs — `Iverson.Client.Contracts.csproj:17`, `Java/client/pom.xml:104`, and the `generate_protos.sh` scripts for Python, TS and Go all read `Common/Proto`
- SDK convention is a builder plus a coordinator method — `DotNet/.../EntityCoordinator.cs:296` `PipelineAsync(PipelineBuilder)`; `PipelineBuilder` in DotNet, Java and Go; `core.py`, `core.ts`
- Pipeline-style authorization, denied → empty stream, and its exception mapping — `ObjectSearchGrpcService.cs:860-927`
- Tenant column and hidden fields are excluded from referenceable columns — `StarRocksPipelineBuilder.cs:39-57` (`ColumnsFor`)
- StarRocks tenant wrapper cannot stream — `EngagementRepository.cs:158-190` (`RunTenantScopedAsync` returns `Task<T>`; `await using var conn`); callers use buffered `conn.QueryAsync` (`:361`)
- Chunk filter, ownership, `AllowedFields` and scoped API key — `ObjectSearchGrpcService.cs:495-545`, `:634-650`
- Chunk payload is `parent_id`/`field`/`chunk_index` (string); no index on `chunk_index` — `IntelligenceStoreConsumer.cs:292-295`; no `CreatePayloadIndex` for it
- Qdrant `order_by` requires a range-capable (integer/float/datetime) index — Qdrant indexing docs and issue #1763 (web search 2026-09-17)
- Match-any on keywords and `HasId` exist — `Qdrant.Client` 1.18.1 XML: `Conditions.Match(string, IReadOnlyList<string>)`, `Conditions.HasId(IReadOnlyList<ulong>)`
- Key → point ID; vector naming is `PropertyName.ToSnakeCase() + "_vector"`, with the property resolved case-insensitively from `VectorFields`/`ChunkFields`; chunk `field` holds the canonical property name — `IntelligenceStoreConsumer.cs:650` `KeyToUlong`; `ObjectSearchGrpcService.cs:162-163,253,506-507,638`; CDR-1 probes P2/P7 (Qdrant stores `title_vector`; the unconverted name fails)
- Project references: `Iverson.Vector` → Contracts today, and `Iverson.Patterns` under this design (Contracts itself references no project, so no cycle); `Iverson.Api` → Embeddings, Sql, StarRocks, Vector, Events, Contracts; `ModelOf`, `KeyToUlong`, `ToSnakeCase` are `internal` to Api and `BuildChunksFilter` is `private` — `Iverson.Vector.csproj:26`; `Iverson.Client.Contracts.csproj` (no `ProjectReference`); `Iverson.Api.csproj:42-47`; `SchemaDescriptor.cs:27`; `IntelligenceStoreConsumer.cs:650`; `NamingExtensions.cs:5`; `ObjectSearchGrpcService.cs:1130`
- `ColumnsFor` is `private` to `Iverson.StarRocks` and its values are canonical column spellings; the authorization constraint it needs is built at §3.4 step 4; `EngagementQueryTranslationException` is public — `StarRocksPipelineBuilder.cs:39-58`; `ObjectSearchGrpcService.cs:1070-1080`; `EngagementQueryTranslationException.cs:9`; CDR-2 probe P3 (StarRocks returns the stored case)
- `AllowedFields` holds canonical property names in an ordinal `HashSet` (only the excluded set is case-insensitive), and includes every `ChunkFields.PropertyName` — `RowFieldAuthorizationEvaluator.cs:80-116`; CDR-2 probe P26; CDR-1 probes P5, P8b
- `System.Numerics.Tensors` is compile-visible to `Iverson.Api` after the §3.3 relocation: a transitive `PackageReference` from `Iverson.Vector`, with no `PrivateAssets` — `Iverson.Vector.csproj:22,26`; `Iverson.Api.csproj:45`; CDR-2 probe P25
- The `Not existing vector name` message covers every collection shape the codebase can hold, including legacy unnamed-vector configs, and the re-issued no-vector scroll succeeds — CDR-2 probe P23 (Qdrant 1.18.2, real `IntelligenceCollectionManager`/`IntelligenceVectorService`)
- Every `[IversonEmbedding]` property is also a `ColumnsFor` column, so a `SIMILARITY` column passes the §3.2 membership check — `SchemaBuilder.cs:60-64` (every non-key property becomes a `ScalarColumn`, before the `IsEmbedding` branch); the only removals are the tenant column, disallowed fields and bytes columns, each already a stated rejection
- `BuildWhere` silently drops an unresolvable property; `Pipeline` pre-validates with `RequireColumn` — `StarRocksQueryBuilder.cs:636-637`; `StarRocksPipelineBuilder.cs:100-101,408-413`; CDR-1 probe P1
- The field-authorization set holds canonical property names, compared case-sensitively — `ObjectSearchGrpcService.cs:173,517`; CDR-1 probes P5, P8b
- Qdrant 1.18.2 error shapes: missing collection → `NotFound`; missing named vector (retrieve or vector-selecting scroll) → `InvalidArgument "Wrong input: Not existing vector name error: <name>"`; `RetrieveNamedVectorAsync` catches neither; collections are created/migrated only by the consumer on write — `ObjectSearchGrpcService.cs:281-287` (`NotFound` precedent); `IntelligenceVectorService.cs:156-187`; CDR-1 probes P2, P7
- Unbuffered read works against StarRocks 4.1.1 through a raw `MySqlDataReader` (early stop leaves the connection reusable); Dapper `QueryUnbufferedAsync` throws on reader disposal — CDR-1 probe P4
- `.NET` `double` `NaN` semantics as stated in §2 — CDR-1 probes P6, P13, P18
- Output rows are name-keyed `Struct`s: colliding names overwrite or vanish; the tenant-name strip is case-insensitive; names differing only by case survive at the server and in every SDK's untyped map — `SchemaDescriptor.cs:20-21`; `AuthorizationFieldMasking.cs:217-218`; CDR-1 probes P21, P21b, P21c
- Bytes columns are identifiable from the schema: scalar `ClrBytes` → `SqlType` `BYTEA` (StarRocks `VARBINARY`); `BYTEA[]` → `STRING`; `EngagementQuerySchema` carries no column types — `SchemaBuilder.cs:60-64,398,419`; `SchemaDescriptor.cs:117` (`ColumnDescriptor(Name, SqlType, IsNullable)`); `EngagementQuerySchema.cs:39-54`
- Bytes columns break output (`"System.Byte[]"`), default-equality partitioning, expression comparison, and `where` filtering — CDR-1 probes P8e, P13, P14, P16c
- The new proto enum values and message names do not collide inside `package iverson` — CDR-3 §0 S3 greps (both exit 1)
- `Qdrant.Client` 1.18.1 exposes a scroll taking filter, page size, cursor, payload selector and vector selector — CDR-3 probe P29 (`Qdrant.Client.xml` member signature)
- The new `Iverson.Vector` → `Iverson.Patterns` edge is acyclic — CDR-3 probe P29: `Iverson.Client.Contracts.csproj` declares no `ProjectReference`, and §3.1 gives `Iverson.Patterns` no store dependencies
- `MatchRowsAsync` can hold the measure-name comparison's second operand: `ColumnsFor` is in the same assembly and the constraint arrives as a method argument, as it does for `PipelineAsync` — `StarRocksPipelineBuilder.cs:39-55`; `IEngagementStoreRoles.cs:51-55`
- Retrieve-by-ID and local cosine exist; the collection metric is Cosine — `IVectorRoles.cs:17`, `IntelligenceVectorService.cs:156-187`; `ResultReranker.cs:42` `TensorPrimitives.CosineSimilarity`; `IntelligenceCollectionManager.cs:17`
- `SearchNamedAsync` exposes no exact/threshold parameter — `IntelligenceVectorService.cs:124-148`
- Embedding call and its failure mapping — `ObjectSearchGrpcService.cs:549-567`
- Global interceptors, no per-RPC name lists — `Program.cs:92-96`; no server references to RPC method names
- Limits configuration pattern — `Program.cs:261-280`
- `DateTime` converts to `Struct` — `ObjectSearchGrpcService.cs:1261`
- Testcontainers available; Trino image ships a memory catalog — `Iverson.Api.Tests.csproj:24` (Testcontainers 4.15.0); `trinodb/trino` `core/docker/default/etc/catalog/memory.properties`
- Both solution files list every project — `Iverson.slnx`, `Iverson.Server/Iverson.Server.slnx`
- A repeated select-list name is returned twice by the raw reader, and an `Add`-built row dictionary then throws; the post-fix `ONE_ROW` SQL lists each name once — CDR-4 probes P45, P47 (StarRocks 4.1.1, MySqlConnector 2.4.0 raw reader)
- StarRocks names each result column as the `SELECT` spells it; an ordinal lookup of a re-cased name misses, and an `OrdinalIgnoreCase` lookup hits — CDR-4 probe P50 (real `ColumnsFor` by reflection; StarRocks 4.1.1 raw reader)
- The server image restores only the `.csproj` files its Dockerfile copies before `dotnet restore`, then publishes with `--no-restore`; a referenced project missing from that list fails the publish with `NETSDK1004`, and adding its `COPY` line fixes it; the two solution files build and test both new projects — `Iverson.Server/Iverson.Api/Dockerfile:13-23`; CDR-5 probes P61, P62, P64
- The raw reader, and Dapper's `ExecuteReaderAsync` wrapper around it, return `DBNull.Value` for `NULL` in every mapped StarRocks column type, where Dapper's `Query` methods return `null`; `DictToProtoStruct` emits `DBNull.Value` as an empty string and `null` as `null_value` — `ObjectSearchGrpcService.cs` `ToProtoValue` (`null => Value.ForNull()`; default arm `Value.ForString(v.ToString()!)`); CDR-5 probes P66, P67, P69
- The WHERE builder binds `IN` as one `List<string>` parameter, which a plain `MySqlCommand` rejects (`NotSupportedException`); every other clause and the owner/tenant predicates bind scalars. Dapper's `ExecuteReaderAsync(sql, param)` binds all of them, returns a `DbWrappedReader` that streams (no whole-result buffering) and yields `DBNull.Value` for `NULL`, and after an early stop, even over a multi-packet result, leaves the connection usable for `SET ROLE NONE` — `StarRocksQueryBuilder.cs:1006-1066`, `:116-130`; `StarRocksPipelineBuilder.cs:444-465`; `AuthorizationConstraint.cs:6,8`; CDR-6 probes P73, P74, P75, P76, P77, P78
- Each non-`NULL` value the reader yields for a mapped StarRocks column type is `String`, `Int32`, `Int64`, `Single`, `Double`, `Boolean` or `DateTime`, and each has a typed `ToProtoValue` arm; `VARBINARY` is excluded before projection — `ObjectSearchGrpcService.cs` `ToProtoValue`; CDR-6 probe P71
- MySqlConnector 2.4.0 against StarRocks 4.1.1, measured as `root` (CDR-7 P85–P87) and as `iverson_app` under a tenant role provisioned by `EnsureTenantProvisionedAsync` (CDR-7 P88): `ExecuteReaderAsync(sql, param)` takes no token, and its query start ran to completion (6 s) under a 1 s token. While the server is executing the statement, a `CommandDefinition(…, cancellationToken)` start fails at about 1.1 s with `MySqlException` `ParseError` (1064), `IsTransient` false, because StarRocks rejects MySqlConnector's clean-up `SELECT SLEEP(0) INTO @__MySqlConnector__Sleep;`. The connection is left `ClearingPendingCancellation`, `SET ROLE NONE` fails, and the pool gives the next borrower a fresh session. Against a frozen server a token does not help: the query start, a row read, `SET ROLE` and `SET ROLE NONE` each end at MySqlConnector's 30 s command timeout (about 32 s) as `CommandTimeoutExpired` (-1), with or without a token. Disposing a reader after an early stop stays pending until the server resumes, even when the connection is closed instead, and no MySqlConnector call bounds that drain: the token, `MySqlCommand.Cancel()` and `ClearPoolAsync`/`ClearAllPoolsAsync` all leave it pending. A token checked only between rows yields `OperationCanceledException` but cannot interrupt the start. On the `CommandDefinition` path, binding, streaming and early stop behave as on `(sql, param)`, and `KILL QUERY` works for `iverson_app`. `EmbeddingService`, `RetrieveAsync` and `ScrollAsync` honour a token. Without one, each stays pending past 8 s against an endpoint that never answers: the embedding call is bounded only by its `HttpClient` timeout of 100 s, and the Qdrant calls were still pending at 40 s. Both are past the 30 s `TimeoutSeconds` — CDR-7 probes P85, P86, P87, P88, P89, P89c, P95, P96, P98, P98b, P100, and greps G14 and G25; `StarRocksResiliencePipelineFactory.cs:19,37`; `EngagementRepository.cs:142-148`
- `PatternBudget(maxActiveThreads, maxSteps, cancellationToken = default)` observes its token only at matcher steps and thread-equivalence comparisons; evaluating `measures` for output rows observes no token. Without a token, a 10,000-row `A+` / `SUM(A.x) >= 0` / `SKIP TO NEXT ROW` run was still matching at 45 s with 5.46% of `MaxSteps` used; with a token cancelled at 2 s it threw `OperationCanceledException` at 2.0 s. With the token, `ALL_ROWS_SHOW_EMPTY` over one 10,000-row match with 50 `SUM(A.x)` measures and `limit` 10,000 ignored the cancellation and ended normally at 576 s, with at most 2.6 s between output rows — `PatternBudget.cs:11-26`; `Matcher.cs:145,213,220`; `PartitionMatcher.cs:226-232`; CDR-8 probes P1, P1b, P6
- Plan 1's engine (merged `156fb8c9`) exposes `PatternQuery.Compile(PatternRequest, int maxProgramInstructions)` and `CompiledPattern.Run(IReadOnlyList<IDictionary<string, object?>> partitionRows, Func<int, int, double?> similarity, PatternBudget budget)`, which yields output rows lazily; row dictionaries must compare keys with `OrdinalIgnoreCase`; the callback is `similarity(row index within the partition, index into SimilarityTerms)`, with `null` meaning SQL `NULL`; one `PatternBudget` is shared by every partition of a request — `PatternQuery.cs:17`; `CompiledPattern.cs:40-67`; CDR-8 §0 A2, E2–E11, W1–W8

## Verified plan-level assumptions

These assumptions are introduced by this plan and were verified on 2026-09-27 at repo HEAD `3fe355e1`.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | None of the Create paths listed under File Structure exists yet, and `IChunkRowSource`, `ScrollAsync`, `MatchRowsAsync` and `PatternQueryLimitOptions` appear nowhere in the repository | Plan-2 dossiers (StarRocks, Vector, Api): repository-wide greps for each symbol returned 0 hits outside `.worktrees/` |
| 2 | File path | Every Modify path exists at the stated location, including the two hand-written test fakes of `IEngagementStoreSearchService` (`AdminConsoleDataVolumeEndpointTests.cs:108-134` `SearchServiceBase`; `Helpers/AdminConsoleTestWebApplicationFactory.cs:185-221` `AdminConsoleSearchService`) | StarRocks dossier §2: grep `": .*IEngagementStoreSearchService"` finds 4 real implementers. The positive control is `EngagementRepository.cs:17`; the other hits are `Substitute.For` mocks |
| 3 | Signature | `IEngagementStoreSearchService` today has `SearchAsync`, `AggregateAsync`, `GroupByAsync` and `PipelineAsync`, and no member returns `IAsyncEnumerable` | `IEngagementStoreRoles.cs:45-77` read |
| 4 | Signature | `StarRocksPipelineBuilder` and `StarRocksQueryBuilder` are `internal static` classes. `ColumnsFor(EngagementQuerySchema, AuthorizationConstraint?)` is `private static` and returns an `OrdinalIgnoreCase` dictionary mapping each name to its canonical spelling; it includes the key and excludes the tenant column and disallowed fields. `BuildWhere(schema, clauses, logic, DynamicParameters, out int, tableMap, authz)` is `internal static`, returns only the fragment, binds parameters into the caller's `DynamicParameters`, throws `EngagementQueryTranslationException` for a disallowed field, and silently skips an unresolvable property | `StarRocksPipelineBuilder.cs:19,39-58`; `StarRocksQueryBuilder.cs:16,555-601,616-655` |
| 5 | Signature | The authorization predicates are appended the same way in `BuildSearch`, `BuildAggregate`, `BuildGroupBy` and `StarRocksPipelineBuilder.Build`: `(where) AND \`OwnerColumn\` = @__ownerVal`, then `(where) AND \`TenantColumn\` = @__tenantVal` | `StarRocksQueryBuilder.cs:109-132`; `StarRocksPipelineBuilder.cs:444-465` |
| 6 | Signature | `EngagementRepository` has these members:<br>- `CreateConnection()` (`:30`)<br>- `RunAsync<T>(Func<Task<T>>)` (`:36-51`), which runs the readiness gate and then `_pipeline` (circuit breaker and retry), mapping `BrokenCircuitException` to `EngagementNotReadyException`<br>- `RunTenantScopedAsync` (`:159-218`), which wraps open + `SET ROLE` + the whole operation + `SET ROLE NONE` in one `RunAsync` call and swallows a failed `SET ROLE NONE` with a warning<br>- `IsExpectedMissingResourceError` (`:142-148`)<br>- `SearchAsync`'s tenant handling (`:328-367`): a null or invalid tenant returns `[]`; with `authz == null` the query runs unscoped | StarRocks dossier §3, read verbatim |
| 7 | Signature | Dapper 2.1.89 exposes `ExecuteReaderAsync(this DbConnection, CommandDefinition)`, which returns `Task<DbDataReader>`. The repository has no existing use of that overload | Decompiled `Dapper.dll` 2.1.89 `SqlMapper:4719`; StarRocks dossier §11 |
| 8 | Signature | `IVectorQueryService` (`IVectorRoles.cs:29-55`) has no `CancellationToken` anywhere. Its only production implementer is `IntelligenceVectorService`. The only production caller of `RetrieveNamedVectorAsync` is `ObjectSearchGrpcService.RetrieveVectorsOrDegradeAsync` (`:967`), which passes no token. `IntelligenceVectorService.ToCanonicalString(Value)` is `internal static` (`:254`) | Vector dossier §1–§3; `IntelligenceVectorService.cs:254` read |
| 9 | Signature | `QdrantClient.ScrollAsync` (1.18.1) has the signature `(string collectionName, Filter? filter = null, uint limit = 10, PointId? offset = null, WithPayloadSelector? payloadSelector = null, WithVectorsSelector? vectorsSelector = null, ReadConsistency? = null, ShardKeySelector? = null, OrderBy? = null, CancellationToken cancellationToken = default)` and returns `Task<ScrollResponse>`, whose `Result` is a list of `RetrievedPoint` and whose `NextPageOffset` is a `PointId` (null on the last page). `string[]` converts implicitly to both selectors. A vector is read as `v.Dense?.Data ?? v.Data` | Decompiled `Qdrant.Client.dll` 1.18.1 (Vector dossier §4); `IntelligenceVectorService.cs:181` |
| 10 | Signature | `Qdrant.Client.RequestHeaders.Use(string, string)` is an `AsyncLocal` scope that restores the previous headers on `Dispose`. The code therefore wraps each individual Qdrant call in its own scope, and never holds one across a `yield` | Decompiled `Qdrant.Client.RequestHeaders` (1.18.1) |
| 11 | Signature | `IntelligenceTenantScope.ResolveCollectionName(baseName, tenantId, isChunks)` and `MintScopedApiKey(collection, readOnly)` exist. A scoped key expires 30 s after minting. `AddQdrant` registers `IntelligenceTenantScope` as a singleton | `IntelligenceTenantScope.cs:40-45,87-103`; `Iverson.Vector/ServiceCollectionExtensions.cs:62` |
| 12 | Signature | `IntelligenceFilterBuilder.ApplyOwnership(Filter?, bool, string?, string?)` returns `Filter?`. `Conditions.MatchKeyword(string, string)` and `Conditions.Match(string, IReadOnlyList<string>)` exist; the second is the `IN` form. `Filter.Must` is additive | `IntelligenceFilterBuilder.cs:57-89,97-114` |
| 13 | Signature | In `ObjectSearchGrpcService`, these are all `private`, so a partial-class file can call them:<br>- `RequireSchema`<br>- `EvaluateAuthorization(schema, IEnumerable<string>)` → `AuthzResult(PrimaryDenied, DeniedJoinedType, Constraints)`<br>- `BuildChunksFilter(schema, clauses, allowedFields)`, which throws `RpcException(InvalidArgument)` itself for a non-`EQUALS` or `MUST_NOT` clause<br>- `DictToProtoStruct(Dictionary<string, object?>)`<br>- `ToProtoValue`<br>The class is `sealed`, not `partial` | Api dossier §2a, §2e, §2f |
| 14 | Signature | `AuthorizationDecision(bool Denied, bool OwnershipRequired, string? OwnerFieldName, string? OwnerValue, IReadOnlySet<string>? AllowedFields, string? TenantColumn, string? TenantValue)`. `authEvaluator.Evaluate(schema, actingUser, AuthorizationAction.Read)` returns it | `IRowFieldAuthorizationEvaluator.cs:16-31` read |
| 15 | Signature | `SchemaDescriptor` has:<br>- `TenantColumnName` (`const "__TenantId"`, `:13`)<br>- `CollectionName` (`string?`)<br>- `KeyColumn.Name`<br>- `ScalarColumns` (`ColumnDescriptor(Name, SqlType, IsNullable)`)<br>- `VectorFields` (`VectorDescriptor(PropertyName, Dimension, ModelId)`)<br>- `ChunkFields` (`ChunkDescriptor(PropertyName, …)`)<br>- `Authorization?.OwnerField`<br>- `ModelOf(d)` (internal static)<br>`SchemaBuilder.ToEngagementQuerySchema(d)` is internal static | `SchemaDescriptor.cs:13,20-40,117-126`; `SchemaBuilder.cs:322` |
| 16 | Signature | `IEmbeddingService.EmbedQueryAsync(string text, CancellationToken ct = default)` exists, and `resolver.Get(SchemaDescriptor.ModelOf(schema))` returns it. `IntelligenceStoreConsumer.KeyToUlong(string)` is `internal static` in `Iverson.Api`. Chunk payloads carry `text`, `parent_id`, `field` (the canonical `PropertyName`) and `chunk_index`, which is stored as a string. The chunk vector's name is `PropertyName.ToSnakeCase() + "_vector"`, and chunks live in the `isChunks: true` collection | `IEmbeddingService.cs:1-11`; `IntelligenceStoreConsumer.cs:215,290-296,645-661` |
| 17 | Code validity | Adding the spec §1 proto block to `object_search.proto` compiles under `Grpc.Tools` 2.81.1. It generates:<br>- the enums `PatternRowSource.{TypeRows, Chunks}`, `RowsPerMatch.{OneRow, AllRowsShowEmpty, AllRowsOmitEmpty, AllRowsWithUnmatched}` and `AfterMatchSkipKind.{PastLastRow, ToNextRow, ToFirst, ToLast}`<br>- the properties `ChunkProperty`, `WhereLogic`, `PartitionBy` (`RepeatedField<string>`), `AfterMatch` (`AfterMatchSkip`), `MatchNumber` (`long`) and `Classifier`<br>- the base method `ObjectSearchServiceBase.MatchPattern(MatchPatternRequest, IServerStreamWriter<MatchPatternResponse>, ServerCallContext)`<br>`RowsPerMatch` and `AfterMatchSkipKind` exist in both `Iverson.Client.Contracts` and `Iverson.Patterns`, so the RPC file aliases the engine's | Scratch build of `Iverson.Client.Contracts` with the block appended: `Build succeeded`; generated `ObjectSearch.cs:270-286,9679-9793,11009-11021`, `ObjectSearchGrpc.cs:177` |
| 18 | Code validity | With a deadline, `ServerCallContext.CancellationToken` in Grpc.AspNetCore 2.83.0 is the deadline manager's token, which is cancelled when the deadline passes. So a token source linked to it, plus `CancelAfter(TimeoutSeconds)`, fires at the earlier of the two. That is §5's "30, or the client deadline if earlier" | Decompiled `HttpContextServerCallContext.CancellationTokenCore` (`DeadlineManager?.CancellationToken ?? HttpContext.RequestAborted`) and `ServerCallDeadlineManager` (`_deadlineCts.Cancel()` on deadline) |
| 19 | Command | Podman on this host pauses and unpauses containers, and Testcontainers 4.15.0 `IContainer` exposes `PauseAsync(CancellationToken)` and `UnpauseAsync(CancellationToken)`. No existing test uses them | Probe: `docker pause` then `inspect` gives `paused`, and `unpause` gives `running`; `Testcontainers.xml` members; grep `PauseAsync` gives 0 hits |
| 20 | Command | CI runs `dotnet build Iverson.slnx`, `dotnet test Iverson.slnx --filter "Category!=Integration"`, the TypeScript SDK's `npm ci && npm test`, and a CodeQL Java build (`mvn … clean install`, which compiles `Common/Proto` at build time). No job regenerates or diffs the committed Go, Python or TypeScript protobuf code, so the SDKs keep compiling until Plan 3 regenerates them | `.github/workflows/dotnet-build.yml:19-20,30-32`; `codeql.yml:72`; the generated files are committed (`git ls-files`) |
| 21 | Command | Commit messages are lowercase imperative sentences | `git log --format=%s -8` |
| 22 | Consumer impact | Adding an optional trailing `CancellationToken ct = default` to `RetrieveNamedVectorAsync` leaves the ~30 existing NSubstitute setups and `Received` checks matching the existing no-token production call. A call that passes a real token does **not** match them, so new tests configure `Arg.Any<CancellationToken>()` | Scratch NSubstitute 5.3.0 probe: a 3-arg `Arg.Any` setup gives `no-token call matched: True`, `real-token call matched: False`, and `Received(3-arg) ok` |
| 23 | Consumer impact | `ObjectSearchGrpcService` is constructed in 16 places, all in tests (`ObjectSearchGrpcServiceTests.cs` ×15, `DocumentTemplateValidationTests.cs:430`), plus DI in `Program.cs:720`. Two trailing optional parameters leave every call site compiling | `grep "new ObjectSearchGrpcService("` |
| 24 | Consumer impact | `QdrantVectorServiceTests.cs:251` asserts that `IntelligenceVectorService` implements `IVectorQueryService`, so the new `ScrollAsync` must be implemented there. Every other test double is `Substitute.For<IVectorQueryService>()`, which picks up the new member automatically | Vector dossier §2 |
| 25 | Consumer impact | The `Iverson.Server/Iverson.Api/Dockerfile` restores only the `.csproj` files it copies before `RUN dotnet restore` (`:13-20`). `Iverson.Api` referencing `Iverson.Patterns`, directly and through `Iverson.Vector`, therefore needs one new `COPY` line | Api dossier §6; inherited spec row (CDR-5 P61/P62/P64) |
| 26 | Code validity | `EngagementTableSchema(TableName, KeyColumn, Columns)` and `EngagementColumnSchema(Name, SrType, IsNullable)` take StarRocks types directly. `EnsureTenantProvisionedAsync` creates the tenant database, the role, the grant to `'iverson_app'@'%'` and the table. A tenant-scoped read needs the `iverson_app` user set up as in `TenantIsolationIntegrationTests` (`:23-73`) | `EngagementTableSchema.cs:1-14`; `EngagementRepository.cs:272-330`; `TenantIsolationIntegrationTests.cs` |
| 27 | Code validity | `SchemaFixtures.ArticleSchema()` has key `Id`, the scalars `Title`, `Body` and `AuthorId`, a `Title` vector (768), a `Body` chunk field and `CollectionName "articles"`. `AuthorSchema()` has `Name` and `Bio` and no collection. Both grant `test-bypass`. `ActingUserFixtures.Principal("test-user", "test-bypass")` carries `tenant_id = "test-tenant"`, and `TestServerCallContext.Create(ct)` passes a token | `Helpers/SchemaFixtures.cs:39-76`; `Helpers/ActingUserFixtures.cs:11-18`; `Helpers/TestServerCallContext.cs` |
| 28 | Ordering | Task dependencies: 1 → all; 2 → 3; 4 → 5; {3, 4, 5, 6} → 7. Tasks 2, 4 and 6 do not depend on each other | Each task's Interfaces section |
| 29 | Code validity | Primary-constructor parameters of a `partial` class are in scope in every part, so `ObjectSearchGrpcService.MatchPattern.cs` can read `registry`, `search`, `vector`, `resolver`, `tenantScope`, `patternLimits` and `chunkRows` and initialise fields from them | Scratch net10.0 probe: two `partial` files, where the second part reads the first part's primary-constructor parameters and fields, printed `r:0:default:r` |

## Tasks

### Task 1: Proto messages, project references and the partial service

**Files:**
- Modify: `Iverson.Clients/Common/Proto/object_search.proto`
- Modify: `Iverson.Server/Iverson.Api/Iverson.Api.csproj`, `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj`
- Modify: `Iverson.Server/Iverson.Api/Dockerfile`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:199` (the class declaration only)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternProtoTests.cs`

**Interfaces:**
- Produces:
  - the generated `MatchPatternRequest`, `MatchPatternResponse`, `PatternSubset`, `NamedExpr`, `AfterMatchSkip`, `PatternRowSource`, `RowsPerMatch` and `AfterMatchSkipKind` in `Iverson.Client.Contracts`;
  - the `Iverson.Api → Iverson.Patterns` and `Iverson.Vector → Iverson.Patterns` references;
  - `ObjectSearchGrpcService` as a `partial` class.

  Every later task consumes these.

- [ ] **Step 1: Write the failing test**

`Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternProtoTests.cs`. The RPC maps the proto enums onto the engine's by integer value, so this test pins that the two sets are aligned member by member:
```csharp
using FluentAssertions;
using Xunit;
using Proto = Iverson.Client.Contracts;
using Engine = Iverson.Patterns;

namespace Iverson.Api.Tests.Grpc;

public sealed class MatchPatternProtoTests
{
    [Fact]
    public void Proto_enums_align_with_the_engine_enums_by_value_and_name()
    {
        AssertAligned<Proto.PatternRowSource, Engine.PatternSource>();
        AssertAligned<Proto.RowsPerMatch, Engine.RowsPerMatch>();
        AssertAligned<Proto.AfterMatchSkipKind, Engine.AfterMatchSkipKind>();
    }

    private static void AssertAligned<TProto, TEngine>() where TProto : struct, Enum where TEngine : struct, Enum
    {
        var proto = Enum.GetValues<TProto>().Select(v => (Convert.ToInt32(v), v.ToString())).ToList();
        var engine = Enum.GetValues<TEngine>().Select(v => (Convert.ToInt32(v), v.ToString())).ToList();
        proto.Should().Equal(engine);
    }

    [Fact]
    public void MatchPattern_request_and_response_carry_the_spec_fields()
    {
        var request = new Proto.MatchPatternRequest
        {
            TypeName = "Article", Source = Proto.PatternRowSource.Chunks, ChunkProperty = "Body",
            WhereLogic = Proto.SearchLogic.And, Pattern = "A B+", Limit = 5, TraceId = "t",
            RowsPerMatch = Proto.RowsPerMatch.AllRowsWithUnmatched,
            AfterMatch = new Proto.AfterMatchSkip { Kind = Proto.AfterMatchSkipKind.ToFirst, Variable = "B" },
        };
        request.PartitionBy.Add("AuthorId");
        request.OrderBy.Add(new Proto.SearchSort { Property = "Id" });
        request.Subsets.Add(new Proto.PatternSubset { Name = "U", Variables = { "A", "B" } });
        request.Define.Add(new Proto.NamedExpr { Name = "B", Expr = "B.x > 0" });
        request.Measures.Add(new Proto.NamedExpr { Name = "m", Expr = "COUNT(*)" });

        var roundTripped = Proto.MatchPatternRequest.Parser.ParseFrom(request.ToByteArray());
        roundTripped.Should().Be(request);

        var response = new Proto.MatchPatternResponse { MatchNumber = 3, Classifier = "B", TraceId = "t" };
        response.MatchNumber.Should().Be(3L);
    }
}
```
The test needs `using Google.Protobuf;` for `ToByteArray`; add it to the usings.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~MatchPatternProtoTests"`
Expected: build FAIL. `MatchPatternRequest`, `PatternRowSource` and `Iverson.Patterns` are not visible yet.

- [ ] **Step 3: Add the RPC and messages to the proto**

In `Iverson.Clients/Common/Proto/object_search.proto`, add this line to `service ObjectSearchService`, directly after the `Pipeline` RPC:
```proto
    rpc MatchPattern   (MatchPatternRequest)  returns (stream MatchPatternResponse);
```
Append to the end of the file, with the text and comments exactly as in spec §1:
```proto

// ── MatchPattern (SQL:2016 row pattern recognition) ─────────────────────────

message MatchPatternRequest {
    string                 type_name      = 1;
    PatternRowSource       source         = 2;  // default TYPE_ROWS
    string                 chunk_property = 3;  // CHUNKS only: an [IversonChunk] property
    repeated SearchClause  where          = 4;  // pre-filter
    SearchLogic            where_logic    = 5;
    repeated string        partition_by   = 6;  // TYPE_ROWS only
    repeated SearchSort    order_by       = 7;  // TYPE_ROWS: required, >= 1 entry
    string                 pattern        = 8;
    repeated PatternSubset subsets        = 9;
    repeated NamedExpr     define         = 10;
    repeated NamedExpr     measures       = 11;
    RowsPerMatch           rows_per_match = 12; // default ONE_ROW
    AfterMatchSkip         after_match    = 13; // default PAST_LAST_ROW
    int32                  limit          = 14; // output rows; 0 = default
    string                 trace_id       = 15;
}

enum PatternRowSource   { TYPE_ROWS = 0; CHUNKS = 1; }
enum RowsPerMatch       { ONE_ROW = 0; ALL_ROWS_SHOW_EMPTY = 1; ALL_ROWS_OMIT_EMPTY = 2;
                          ALL_ROWS_WITH_UNMATCHED = 3; }
enum AfterMatchSkipKind { PAST_LAST_ROW = 0; TO_NEXT_ROW = 1; TO_FIRST = 2; TO_LAST = 3; }

message PatternSubset  { string name = 1; repeated string variables = 2; }
message NamedExpr      { string name = 1; string expr = 2; }
message AfterMatchSkip { AfterMatchSkipKind kind = 1; string variable = 2; } // variable: TO_FIRST/TO_LAST only

message MatchPatternResponse {
    google.protobuf.Struct data         = 1;
    int64                  match_number = 2; // 0 for an unmatched row
    string                 classifier   = 3; // ALL_ROWS_* only; empty for unmatched rows and ONE_ROW
    string                 trace_id     = 4;
}
```

- [ ] **Step 4: Add the project references and the Dockerfile line**

In `Iverson.Server/Iverson.Api/Iverson.Api.csproj`, in the `ProjectReference` item group, add the following after the `Iverson.Vector` line:
```xml
    <ProjectReference Include="..\Iverson.Patterns\Iverson.Patterns.csproj" />
```
In `Iverson.Server/Iverson.Vector/Iverson.Vector.csproj`, in the `ProjectReference` item group, add the following after the `Iverson.Client.Contracts` line:
```xml
    <ProjectReference Include="../Iverson.Patterns/Iverson.Patterns.csproj" />
```
In `Iverson.Server/Iverson.Api/Dockerfile`, add the following after the `Iverson.Vector.csproj` `COPY` line:
```dockerfile
COPY ["Iverson.Server/Iverson.Patterns/Iverson.Patterns.csproj",                                     "Iverson.Server/Iverson.Patterns/"]
```

- [ ] **Step 5: Make the service partial**

In `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:199`, change `public sealed class ObjectSearchGrpcService(` to `public sealed partial class ObjectSearchGrpcService(`. Leave the rest of the file unchanged. Task 7 adds the RPC in `ObjectSearchGrpcService.MatchPattern.cs`, so it can use the class's private helpers without growing this 1,308-line file.

- [ ] **Step 6: Run the tests and the build**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~MatchPatternProtoTests"`
Expected: PASS (2 tests).

Run: `dotnet build Iverson.slnx`
Expected: `0 Error(s)`. Until Task 7, the generated base `MatchPattern` answers `Unimplemented`.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Clients/Common/Proto/object_search.proto Iverson.Server/Iverson.Api/Iverson.Api.csproj Iverson.Server/Iverson.Vector/Iverson.Vector.csproj Iverson.Server/Iverson.Api/Dockerfile Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternProtoTests.cs
git commit -m "add the MatchPattern proto messages and reference the pattern engine"
```

### Task 2: `MatchRowsQueryBuilder`, the TYPE_ROWS validation and SQL (no I/O)

**Files:**
- Create: `Iverson.Server/Iverson.StarRocks/MatchRowsRequest.cs`, `Iverson.Server/Iverson.StarRocks/MatchRowsQueryBuilder.cs`
- Modify: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs:48` (`ColumnsFor`: `private static` becomes `internal static`; nothing else changes)
- Test: `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsQueryBuilderTests.cs`

**Interfaces:**
- Consumes:
  - `StarRocksPipelineBuilder.ColumnsFor`
  - `StarRocksQueryBuilder.BuildWhere` (the schema-resolving overload)
  - `TenantIdentifier.Qualify`
- Produces:
  - `MatchRowsRequest`, consumed by Task 3 and Task 7
  - `MatchRowsQueryBuilder.Build`, consumed by Task 3

- [ ] **Step 1: Create the request record**

`Iverson.Server/Iverson.StarRocks/MatchRowsRequest.cs`:
```csharp
using Iverson.Client.Contracts;

namespace Iverson.StarRocks;

/// <summary>
/// The TYPE_ROWS parts of a MatchPattern request that decide which rows are read and how (spec §3.2).
/// <see cref="ReferencedColumns"/> are the columns <c>define</c>/<c>measures</c>/<c>SIMILARITY</c> read, as the
/// engine reports them (<c>CompiledPattern.ReferencedColumns</c>); <see cref="ExcludedColumns"/> are the type's
/// bytes columns, removed from the <c>ColumnsFor</c> set before validation and projection.
/// </summary>
public sealed record MatchRowsRequest(
    IReadOnlyList<SearchClause> Where,
    SearchLogic WhereLogic,
    IReadOnlyList<string> PartitionBy,
    IReadOnlyList<SearchSort> OrderBy,
    IReadOnlyCollection<string> ReferencedColumns,
    IReadOnlyList<string> MeasureNames,
    bool OneRowPerMatch,
    IReadOnlyCollection<string> ExcludedColumns,
    int MaxRowsScanned);
```

- [ ] **Step 2: Write the failing tests**

`Iverson.Server/Iverson.StarRocks.Tests/MatchRowsQueryBuilderTests.cs`:
```csharp
using Dapper;
using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.StarRocks.Tests;

public sealed class MatchRowsQueryBuilderTests
{
    // "Blob" is a bytes column (passed as excluded); "__TenantId" is the server-owned tenant column.
    private static EngagementQuerySchema Schema() => new(
        "Event", "events", "Id", ["UserId", "Kind", "At", "Score", "OwnerId", "Secret", "Blob", "__TenantId"],
        TenantColumnName: "__TenantId");

    private static IReadOnlyDictionary<string, AuthorizationConstraint> Authz(
        IReadOnlySet<string>? allowed = null, string? ownerColumn = null) =>
        new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = new(allowed, ownerColumn, ownerColumn is null ? null : "u1", "__TenantId", "t1")
        };

    private static MatchRowsRequest Req(
        string[]? partitionBy = null, (string Property, bool Descending)[]? orderBy = null,
        string[]? referenced = null, string[]? measures = null, bool oneRow = true,
        SearchClause[]? where = null, SearchLogic logic = SearchLogic.And, int maxRowsScanned = 100) =>
        new(where ?? [], logic, partitionBy ?? [],
            (orderBy ?? [("At", false)]).Select(o => new SearchSort { Property = o.Property, Descending = o.Descending }).ToList(),
            referenced ?? [], measures ?? [], oneRow, ["Blob"], maxRowsScanned);

    private static (string Sql, DynamicParameters Param) Build(MatchRowsRequest request,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null) =>
        MatchRowsQueryBuilder.Build(Schema(), request, authz ?? Authz(), "iverson_tenant_t1");

    [Fact]
    public void One_row_selects_the_key_then_each_partition_and_referenced_column_once_in_canonical_spelling()
    {
        var (sql, param) = Build(Req(partitionBy: ["userid"], referenced: ["score", "UserId", "KIND"]));

        sql.Should().Be(
            "SELECT `Id`, `UserId`, `Score`, `Kind` FROM `iverson_tenant_t1`.`events` " +
            "WHERE `__TenantId` = @__tenantVal ORDER BY `UserId`, `At`, `Id` LIMIT 101");
        param.Get<string>("__tenantVal").Should().Be("t1");
    }

    [Fact]
    public void All_rows_selects_every_visible_column_except_bytes_and_tenant()
    {
        var (sql, _) = Build(Req(oneRow: false));

        sql.Should().StartWith("SELECT `Id`, `UserId`, `Kind`, `At`, `Score`, `OwnerId`, `Secret` FROM ");
    }

    [Fact]
    public void Order_by_keeps_descending_and_ends_with_the_key_tie_breaker()
    {
        var (sql, _) = Build(Req(partitionBy: ["Kind"], orderBy: [("At", true), ("score", false)]));

        sql.Should().EndWith("ORDER BY `Kind`, `At` DESC, `Score`, `Id` LIMIT 101");
    }

    [Fact]
    public void Where_owner_and_tenant_predicates_compose_as_the_other_read_paths_do()
    {
        var clause = new SearchClause
        {
            Property = "kind", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "view" },
            ClauseType = SearchClauseType.Filter,
        };
        var (sql, param) = Build(Req(where: [clause]), Authz(ownerColumn: "OwnerId"));

        sql.Should().Contain("WHERE ((`Kind` = @p0) AND `OwnerId` = @__ownerVal) AND `__TenantId` = @__tenantVal ORDER BY");
        param.Get<string>("p0").Should().Be("view");
        param.Get<string>("__ownerVal").Should().Be("u1");
    }

    public static TheoryData<string, string> BadColumns() => new()
    {
        { "Nope", "unknown" },
        { "Secret", "hidden" },            // not in AllowedFields (see the Authz below)
        { "__tenantid", "tenant" },
        { "Blob", "bytes" },
    };

    [Theory]
    [MemberData(nameof(BadColumns))]
    public void Every_slot_rejects_an_unknown_hidden_tenant_or_bytes_column(string column, string _)
    {
        var allowed = new HashSet<string> { "Id", "UserId", "Kind", "At", "Score", "OwnerId", "Blob", "__TenantId" };
        var where = new SearchClause
        {
            Property = column, Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "x" },
            ClauseType = SearchClauseType.Filter,
        };

        foreach (var request in new[]
        {
            Req(partitionBy: [column]),
            Req(orderBy: [(column, false)]),
            Req(where: [where]),
            Req(referenced: [column]),
        })
        {
            var act = () => Build(request, Authz(allowed));
            act.Should().Throw<EngagementQueryTranslationException>().WithMessage($"*'{column}'*");
        }
    }

    [Fact]
    public void One_row_rejects_a_measure_named_like_a_partition_column_ordinally_only()
    {
        var act = () => Build(Req(partitionBy: ["userid"], measures: ["UserId"]));
        act.Should().Throw<EngagementQueryTranslationException>().WithMessage("*UserId*");

        Build(Req(partitionBy: ["userid"], measures: ["userid"]));          // differs from the canonical spelling
        Build(Req(partitionBy: ["userid"], measures: ["Kind"]));            // not a ONE_ROW output column
    }

    [Fact]
    public void All_rows_rejects_a_measure_named_like_any_output_column()
    {
        foreach (var name in new[] { "Id", "Kind", "Secret" })
        {
            var act = () => Build(Req(oneRow: false, measures: [name]));
            act.Should().Throw<EngagementQueryTranslationException>().WithMessage($"*{name}*");
        }

        Build(Req(oneRow: false, measures: ["kind"]));                       // ordinal: not an output column
        Build(Req(oneRow: false, measures: ["Blob"]));                       // excluded, so not an output column
    }

    [Fact]
    public void Validation_runs_before_any_sql_is_built_even_with_no_where()
    {
        var act = () => MatchRowsQueryBuilder.Build(Schema(), Req(partitionBy: ["Nope"]), Authz(), null);
        act.Should().Throw<EngagementQueryTranslationException>();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "FullyQualifiedName~MatchRowsQueryBuilderTests"`
Expected: build FAIL, because `MatchRowsQueryBuilder` does not exist.

- [ ] **Step 4: Implement the builder**

In `StarRocksPipelineBuilder.cs:48`, change `private static Dictionary<string, string> ColumnsFor(` to `internal static Dictionary<string, string> ColumnsFor(`.

Create `Iverson.Server/Iverson.StarRocks/MatchRowsQueryBuilder.cs` as an `internal static class MatchRowsQueryBuilder` with this method:
```csharp
internal static (string Sql, DynamicParameters Param) Build(
    EngagementQuerySchema schema, MatchRowsRequest request,
    IReadOnlyDictionary<string, AuthorizationConstraint> authz, string? tenantDatabase)
```
It follows these rules, in order. Every rejection throws `EngagementQueryTranslationException`, and each message names the offending name in single quotes.

1. **Visible set.** Look up the constraint with `authz.TryGetValue(schema.TypeName, out var c)`, then call `StarRocksPipelineBuilder.ColumnsFor(schema, c)`. From the result, remove every key in `request.ExcludedColumns`, compared `OrdinalIgnoreCase`.
2. **Membership**, before any SQL is built. Check, in slot order:
   - every `PartitionBy` entry;
   - every `OrderBy[i].Property`;
   - every `Where[i].Property`;
   - every `ReferencedColumns` entry.

   Each must be a key of the visible set. Otherwise throw with the message `$"MatchPattern: unknown column '{name}' in {slot}."`, where `slot` is `partition_by`, `order_by`, `where` or `define/measures`.
3. **Measure-name collision** (spec §1). Build the output columns:
   - for `OneRowPerMatch`, the canonical spellings (`visible[name]`) of the `PartitionBy` entries;
   - otherwise, every value of the visible set.

   A `MeasureNames` entry equal to one of them under `StringComparison.Ordinal` throws `$"MatchPattern: measure name '{m}' collides with the output column '{c}'."`.
4. **WHERE.** Create `var param = new DynamicParameters();`, then call `StarRocksQueryBuilder.BuildWhere(schema, request.Where, request.WhereLogic, param, out _, null, authz)`. Append the owner predicate and then the tenant predicate exactly as `StarRocksQueryBuilder.cs:109-132` does:
   - `(where) AND \`OwnerColumn\` = @__ownerVal` when the constraint's `OwnerColumn` is not null;
   - `(where) AND \`TenantColumn\` = @__tenantVal` when its `TenantColumn` is not null.
5. **Projection.** Start with `` `key` `` (`schema.KeyColumnName`). Then add the canonical columns, each once, skipping the key:
   - `OneRowPerMatch`: the `PartitionBy` entries, then the `ReferencedColumns`, in first-appearance order;
   - otherwise: the visible set's values in enumeration order (`ColumnsFor` inserts the key first, then `schema.ColumnNames` in order).
6. **ORDER BY.** The canonical `PartitionBy` columns, then each canonical `OrderBy` column (with `` DESC`` when `Descending`), then `` `key` ``.
7. **SQL.** Emit, as one line:
   ```
   $"SELECT {projection} FROM {TenantIdentifier.Qualify(tenantDatabase, schema.TableName)}{(where.Length > 0 ? " WHERE " + where : "")} ORDER BY {orderBy} LIMIT {request.MaxRowsScanned + 1}"
   ```
   The `LIMIT` operand is an `int`, and every identifier comes from the visible set.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "FullyQualifiedName~MatchRowsQueryBuilderTests"`
Expected: PASS (7 tests; the theory runs 4 cases).

Run: `dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "Category!=Integration"`
Expected: PASS. The pipeline builder tests are unchanged by the visibility change.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks/MatchRowsRequest.cs Iverson.Server/Iverson.StarRocks/MatchRowsQueryBuilder.cs Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs Iverson.Server/Iverson.StarRocks.Tests/MatchRowsQueryBuilderTests.cs
git commit -m "add the MatchPattern row-query builder with column and measure-name validation"
```

### Task 3: `MatchRowsAsync`, the streaming tenant-scoped TYPE_ROWS source

**Files:**
- Modify: `Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs` (add the interface member)
- Modify: `Iverson.Server/Iverson.StarRocks/EngagementRepository.cs` (add `MatchRowsAsync`, `StreamTenantScopedAsync`, `ReleaseAsync`)
- Modify: `Iverson.Server/Iverson.StarRocks/DisabledEngagementStoreSearchService.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/AdminConsoleDataVolumeEndpointTests.cs:108-134` (`SearchServiceBase`), `Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs:185-221` (`AdminConsoleSearchService`)
- Modify: `Iverson.Server/Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs` (`StarRocksContainerFixture` gains `PauseAsync`/`UnpauseAsync`)
- Test: `Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs`

**Interfaces:**
- Consumes: `MatchRowsRequest` and `MatchRowsQueryBuilder.Build` (Task 2).
- Produces: `IEngagementStoreSearchService.MatchRowsAsync`, which Task 7 consumes.

- [ ] **Step 1: Write the failing integration tests**

First, add these two methods to `StarRocksContainerFixture` (`StarRocksIntegrationTests.cs`), after `InitializeAsync`:
```csharp
    /// <summary>Freezes the StarRocks container (spec §9.3 pause test). Always pair with <see cref="UnpauseAsync"/>
    /// in a <c>finally</c>: every StarRocks test class shares this one container.</summary>
    public Task PauseAsync() => _container.PauseAsync();

    public Task UnpauseAsync() => _container.UnpauseAsync();
```

`Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs`. The setup follows `TenantIsolationIntegrationTests` (`:23-73`): a tenant-scoped read needs `iverson_app` and a provisioned tenant role.
```csharp
using System.Diagnostics;
using System.Globalization;
using Dapper;
using FluentAssertions;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

[Trait("Category", "Integration")]
[Collection(StarRocksCollection.Name)]
public sealed class MatchRowsIntegrationTests
{
    private readonly StarRocksContainerFixture _fx;
    private readonly EngagementRepository _rootRepo;
    private readonly EngagementRepository _appRepo;

    public MatchRowsIntegrationTests(StarRocksContainerFixture fx)
    {
        _fx = fx;
        _rootRepo = fx.Repository;
        // Same grants and connection-string shape as TenantIsolationIntegrationTests (see its comments).
        fx.Repository.ExecuteAsync("""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY 'test_pw';
            GRANT user_admin TO 'iverson_app'@'%';
            GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
            GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';
            """).GetAwaiter().GetResult();
        var appConnectionString = new MySqlConnectionStringBuilder(fx.ConnectionString)
        {
            UserID = "iverson_app", Password = "test_pw", Database = ""
        }.ToString();
        _appRepo = new EngagementRepository(appConnectionString, NullLogger<EngagementRepository>.Instance);
    }

    private static readonly (string Id, string UserId, string Kind, string At, double? Score, string OwnerId)[] Events =
    [
        ("1", "u1", "view", "2026-01-01 00:00:01", 1.0, "u1"),
        ("2", "u1", "save", "2026-01-01 00:00:02", 2.0, "u2"),
        ("3", "u2", "cite", "2026-01-01 00:00:03", 3.0, "u1"),
        ("4", "u2", "view", "2026-01-01 00:00:03", null, "u2"),   // ties with 3 on At
    ];

    private async Task<(string Tenant, EngagementQuerySchema Schema)> SeedAsync(
        params (string Id, string UserId, string Kind, string At, double? Score, string OwnerId)[] rows)
    {
        var tenant = "m" + Guid.NewGuid().ToString("N")[..16];
        var table = "ev_" + Guid.NewGuid().ToString("N")[..8];
        await _appRepo.EnsureTenantProvisionedAsync(tenant, new EngagementTableSchema(table,
            new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [
                new("UserId", "VARCHAR(36)", true), new("Kind", "STRING", true), new("At", "DATETIME", true),
                new("Score", "DOUBLE", true), new("OwnerId", "STRING", true), new("Blob", "VARBINARY", true),
                new("__TenantId", "STRING", true),
            ]));
        if (rows.Length > 0)
            await _rootRepo.ExecuteAsync(
                $"INSERT INTO `{TenantIdentifier.DatabaseName(tenant)}`.`{table}` " +
                "(`Id`, `UserId`, `Kind`, `At`, `Score`, `OwnerId`, `__TenantId`) VALUES " +
                string.Join(", ", rows.Select(r =>
                    $"('{r.Id}', '{r.UserId}', '{r.Kind}', '{r.At}', " +
                    $"{(r.Score is { } s ? s.ToString(CultureInfo.InvariantCulture) : "NULL")}, '{r.OwnerId}', '{tenant}')")));
        return (tenant, new EngagementQuerySchema("Event", table, "Id",
            ["UserId", "Kind", "At", "Score", "OwnerId", "Blob", "__TenantId"], TenantColumnName: "__TenantId"));
    }

    private static IReadOnlyDictionary<string, AuthorizationConstraint> Authz(string tenant, string? owner = null) =>
        new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = new(null, owner is null ? null : "OwnerId", owner, "__TenantId", tenant)
        };

    private static MatchRowsRequest Req(
        string[]? partitionBy = null, (string Property, bool Descending)[]? orderBy = null, string[]? referenced = null,
        bool oneRow = true, SearchClause[]? where = null, int maxRowsScanned = 100_000) =>
        new(where ?? [], SearchLogic.And, partitionBy ?? [],
            (orderBy ?? [("At", false)]).Select(o => new SearchSort { Property = o.Property, Descending = o.Descending }).ToList(),
            referenced ?? [], [], oneRow, ["Blob"], maxRowsScanned);

    private static async Task<List<IDictionary<string, object?>>> ReadAllAsync(IAsyncEnumerable<IDictionary<string, object?>> rows)
    {
        var list = new List<IDictionary<string, object?>>();
        await foreach (var row in rows) list.Add(row);
        return list;
    }

    private static IEnumerable<object?> Ids(IEnumerable<IDictionary<string, object?>> rows) => rows.Select(r => r["Id"]);

    [Fact]
    public async Task Rows_arrive_by_partition_then_order_by_then_the_key_tie_breaker()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema,
            Req(partitionBy: ["UserId"], orderBy: [("At", true)]), Authz(tenant)));

        Ids(rows).Should().Equal("2", "1", "3", "4");   // u1 by At DESC; u2 tied on At, so by key
    }

    [Fact]
    public async Task One_row_projects_each_needed_column_once_under_its_canonical_name_and_lookups_ignore_case()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema,
            Req(partitionBy: ["userid"], referenced: ["score", "USERID"]), Authz(tenant)));

        rows[0].Keys.Should().Equal("Id", "UserId", "Score");
        rows[0]["USERID"].Should().Be("u1");
        rows[0]["score"].Should().Be(1.0);
    }

    [Fact]
    public async Task All_rows_projects_every_visible_column_but_bytes_and_tenant()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(oneRow: false), Authz(tenant)));

        rows[0].Keys.Should().Equal("Id", "UserId", "Kind", "At", "Score", "OwnerId");
    }

    [Fact]
    public async Task A_null_column_value_arrives_as_csharp_null_not_DBNull()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(referenced: ["Score"]), Authz(tenant)));

        rows.Single(r => (string)r["Id"]! == "4")["Score"].Should().BeNull();
    }

    [Fact]
    public async Task The_owner_row_filter_limits_the_rows_read()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz(tenant, owner: "u1")));

        Ids(rows).Should().Equal("1", "3");
    }

    private static SearchClause Clause(string property, SearchOperator op, SearchValue value,
        SearchClauseType type = SearchClauseType.Filter) =>
        new() { Property = property, Operator = op, Value = value, ClauseType = type };

    private static SearchValue Str(string s) => new() { StringVal = s };
    private static SearchValue Num(double d) => new() { NumberVal = d };

    public static TheoryData<string, string[]> Operators() => new()
    {
        { "EQUALS", ["1", "4"] }, { "NOT_EQUALS", ["2", "3"] }, { "CONTAINS", ["3"] }, { "STARTS_WITH", ["2"] },
        { "ENDS_WITH", ["1", "4"] }, { "GREATER_THAN", ["2", "3"] }, { "LESS_THAN", ["1", "2"] },
        { "GREATER_THAN_OR_EQUALS", ["2", "3"] }, { "LESS_THAN_OR_EQUALS", ["1", "2"] }, { "IN", ["2", "3"] },
        { "MUST_NOT", ["2", "3"] },
    };

    [Theory]
    [MemberData(nameof(Operators))]
    public async Task A_where_clause_of_each_operator_filters_the_rows_read(string op, string[] expectedIds)
    {
        var (tenant, schema) = await SeedAsync(Events);
        var clause = op switch
        {
            "EQUALS" => Clause("Kind", SearchOperator.Equals, Str("view")),
            "NOT_EQUALS" => Clause("Kind", SearchOperator.NotEquals, Str("view")),
            "CONTAINS" => Clause("Kind", SearchOperator.Contains, Str("it")),
            "STARTS_WITH" => Clause("Kind", SearchOperator.StartsWith, Str("sa")),
            "ENDS_WITH" => Clause("Kind", SearchOperator.EndsWith, Str("ew")),
            "GREATER_THAN" => Clause("Score", SearchOperator.GreaterThan, Num(1)),
            "LESS_THAN" => Clause("Score", SearchOperator.LessThan, Num(3)),
            "GREATER_THAN_OR_EQUALS" => Clause("Score", SearchOperator.GreaterThanOrEquals, Num(2)),
            "LESS_THAN_OR_EQUALS" => Clause("Score", SearchOperator.LessThanOrEquals, Num(2)),
            "IN" => Clause("Kind", SearchOperator.In, new SearchValue { StringList = new RepeatedString { Values = { "save", "cite" } } }),
            _ => Clause("Kind", SearchOperator.Equals, Str("view"), SearchClauseType.MustNot),
        };

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(where: [clause]), Authz(tenant)));

        Ids(rows).Should().Equal(expectedIds);
    }

    [Fact]
    public async Task An_invalid_request_fails_on_enumeration_before_the_store_is_read()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var act = () => ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(partitionBy: ["Blob"]), Authz(tenant)));

        await act.Should().ThrowAsync<EngagementQueryTranslationException>().WithMessage("*'Blob'*");
    }

    [Fact]
    public async Task One_row_past_MaxRowsScanned_is_read_so_the_caller_can_detect_overflow()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(maxRowsScanned: 2), Authz(tenant)));

        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task More_rows_than_one_batch_stream_through_in_order()
    {
        var (tenant, schema) = await SeedAsync();
        await InsertManyAsync(tenant, schema.TableName, 2_500, kindChars: 4);

        var count = 0;
        string? previous = null;
        await foreach (var row in _appRepo.MatchRowsAsync(schema, Req(orderBy: [("Id", false)]), Authz(tenant)))
        {
            var id = (string)row["Id"]!;
            if (previous is not null) string.CompareOrdinal(previous, id).Should().BeNegative();
            previous = id;
            count++;
        }
        count.Should().Be(2_500);
    }

    [Fact]
    public async Task A_null_or_invalid_tenant_or_a_missing_table_yields_an_empty_sequence()
    {
        var (tenant, schema) = await SeedAsync(Events);

        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz(null!)))).Should().BeEmpty();
        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz("bad tenant!")))).Should().BeEmpty();
        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema with { TableName = "no_such_table" }, Req(), Authz(tenant))))
            .Should().BeEmpty();
        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz("m" + Guid.NewGuid().ToString("N")[..16]))))
            .Should().BeEmpty();                                  // valid tenant id, never provisioned
    }

    [Fact]
    public async Task A_token_cancelled_while_the_query_is_starting_ends_the_read_early_and_the_pool_recovers()
    {
        var (tenant, schema) = await SeedAsync(Events);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        var act = () => ReadAllAsync(_appRepo.StreamTenantScopedAsync(
            "sr.test", tenant, "SELECT SLEEP(6) AS s", new DynamicParameters(), _ => false, cts.Token));

        await act.Should().ThrowAsync<Exception>();          // a MySqlException (ParseError), per spec §3.2
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the 6 s query must not run to completion");
        cts.IsCancellationRequested.Should().BeTrue();

        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz(tenant)))).Should().HaveCount(4);
    }

    [Fact]
    public async Task Disposing_after_an_early_stop_waits_while_StarRocks_is_paused_and_completes_after_unpause()
    {
        // ~10 MB (20,000 rows x 500 chars) cannot sit in client buffers, so disposing the reader must drain
        // the rest from the server (spec Known issues; CDR-7 P98). This premise is why MatchPattern does not
        // await disposal after `limit` (Task 7).
        var (tenant, schema) = await SeedAsync();
        await InsertManyAsync(tenant, schema.TableName, 20_000, kindChars: 500);

        var reader = _appRepo.MatchRowsAsync(schema, Req(oneRow: false), Authz(tenant)).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();

        Task dispose = Task.CompletedTask;
        await _fx.PauseAsync();
        try
        {
            dispose = reader.DisposeAsync().AsTask();
            (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)))).Should().NotBeSameAs(dispose);
        }
        finally
        {
            await _fx.UnpauseAsync();
        }
        await dispose.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private async Task InsertManyAsync(string tenant, string table, int count, int kindChars)
    {
        for (var start = 0; start < count; start += 1_000)
        {
            var values = Enumerable.Range(start, Math.Min(1_000, count - start)).Select(i =>
                $"('w{i:D6}', 'u1', REPEAT('x', {kindChars}), '2026-01-01 00:00:00', NULL, 'o', '{tenant}')");
            await _rootRepo.ExecuteAsync(
                $"INSERT INTO `{TenantIdentifier.DatabaseName(tenant)}`.`{table}` " +
                "(`Id`, `UserId`, `Kind`, `At`, `Score`, `OwnerId`, `__TenantId`) VALUES " + string.Join(", ", values));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "FullyQualifiedName~MatchRowsIntegrationTests"`
Expected: build FAIL, because `MatchRowsAsync` and `StreamTenantScopedAsync` do not exist.

- [ ] **Step 3: Add the interface member and the non-repository implementations**

In `IEngagementStoreRoles.cs`, inside `IEngagementStoreSearchService`, after `PipelineAsync`:
```csharp
    /// <summary>
    /// The TYPE_ROWS source for MatchPattern (spec §3.2). Validates <paramref name="request"/> against
    /// <c>ColumnsFor</c> minus <see cref="MatchRowsRequest.ExcludedColumns"/> and streams the tenant's rows ordered by
    /// partition, order and key, each as an <c>OrdinalIgnoreCase</c> dictionary with SQL <c>NULL</c> as <c>null</c>.
    /// Reads at most <see cref="MatchRowsRequest.MaxRowsScanned"/> + 1 rows; the caller detects the overflow.
    /// A null or invalid tenant, an unprovisioned tenant or a missing table yields an empty sequence.
    /// </summary>
    IAsyncEnumerable<IDictionary<string, object?>> MatchRowsAsync(
        EngagementQuerySchema schema,
        MatchRowsRequest request,
        IReadOnlyDictionary<string, AuthorizationConstraint> authz,
        CancellationToken ct = default);
```
In `DisabledEngagementStoreSearchService.cs`:
- Add
  ```csharp
      public IAsyncEnumerable<IDictionary<string, object?>> MatchRowsAsync(
          EngagementQuerySchema schema, MatchRowsRequest request,
          IReadOnlyDictionary<string, AuthorizationConstraint> authz, CancellationToken ct = default)
          => throw new EngagementStoreDisabledException(Message);
  ```
- Change the message's second sentence to `"Search, aggregate, group-by, pipeline and match-pattern queries require StarRocks."`.

In the two test fakes, `SearchServiceBase` and `AdminConsoleSearchService`, add the same member with the body `=> throw new NotSupportedException();`, as their other unused members have.

- [ ] **Step 4: Implement the streaming wrapper and `MatchRowsAsync` in `EngagementRepository`**

Add `using System.Data.Common;` and `using System.Runtime.CompilerServices;`. Then add the following, after `RunTenantScopedAsync`. Use it as written. The shape of the resilience scope is the point: spec §3.2 has it wrap only open, `SET ROLE` and query start, not the whole read that `RunTenantScopedAsync` wraps.
```csharp
    /// <summary>
    /// Streams a tenant-scoped query row by row (spec §3.2). Unlike <see cref="RunTenantScopedAsync{T}"/>, the
    /// resilience pipeline wraps only open, <c>SET ROLE</c> and query start; a failure after that propagates. The
    /// connection is held until enumeration ends, then <c>SET ROLE NONE</c> runs with the same failure-swallowing
    /// discipline. <paramref name="ct"/> reaches the query start and every read, and is checked before each row.
    /// </summary>
    internal async IAsyncEnumerable<IDictionary<string, object?>> StreamTenantScopedAsync(
        string activityName, string tenantId, string sql, object param,
        Func<Exception, bool> isExpectedMissingResource, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!TenantIdentifier.IsValid(tenantId))
            throw new ArgumentException($"Invalid tenant ID: '{tenantId}'", nameof(tenantId));

        using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        MySqlConnection? conn = null;
        DbDataReader? reader = null;
        try
        {
            (conn, reader) = await RunAsync(async () =>
            {
                var c = CreateConnection();
                try
                {
                    await c.OpenAsync(ct);
                    await c.ExecuteAsync(new CommandDefinition(
                        $"SET ROLE `{TenantIdentifier.RoleName(tenantId)}`", cancellationToken: ct));
                    var r = await c.ExecuteReaderAsync(new CommandDefinition(sql, param, cancellationToken: ct));
                    return (c, r);
                }
                catch
                {
                    await ReleaseAsync(c, null);
                    throw;
                }
            });
        }
        catch (Exception ex) when (isExpectedMissingResource(ex))
        {
            activity?.SetStatus(ActivityStatusCode.Ok);   // an unprovisioned tenant or missing table: empty, as SearchAsync
        }

        if (reader is null) yield break;

        try
        {
            while (await reader.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[reader.GetName(i)] = value is DBNull ? null : value;   // spec §3.2: SQL NULL is C# null
                }
                yield return row;
            }
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            await ReleaseAsync(conn!, reader);
        }
    }

    private async Task ReleaseAsync(MySqlConnection conn, DbDataReader? reader)
    {
        try
        {
            if (reader is not null) await reader.DisposeAsync();
            await conn.ExecuteAsync("SET ROLE NONE");
        }
        catch (Exception ex)
        {
            // Same discipline as RunTenantScopedAsync: releasing must never replace the read's own outcome.
            logger.LogWarning(ex, "Releasing a tenant-scoped StarRocks reader failed (SET ROLE NONE or reader disposal)");
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    public async IAsyncEnumerable<IDictionary<string, object?>> MatchRowsAsync(
        EngagementQuerySchema schema,
        MatchRowsRequest request,
        IReadOnlyDictionary<string, AuthorizationConstraint> authz,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            yield break;

        var (sql, param) = MatchRowsQueryBuilder.Build(schema, request, authz, TenantIdentifier.DatabaseName(tenantId));

        await foreach (var row in StreamTenantScopedAsync("sr.match_rows", tenantId, sql, param, IsExpectedMissingResourceError, ct))
            yield return row;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.StarRocks.Tests --filter "FullyQualifiedName~MatchRowsIntegrationTests"`
Expected: PASS. That is 12 facts plus the 11-case operator theory. The StarRocks container starts on the first run, which takes about 3 minutes.

Run: `dotnet build Iverson.slnx`
Expected: `0 Error(s)`. The two Api test fakes and the disabled service compile.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.StarRocks/IEngagementStoreRoles.cs Iverson.Server/Iverson.StarRocks/EngagementRepository.cs Iverson.Server/Iverson.StarRocks/DisabledEngagementStoreSearchService.cs Iverson.Server/Iverson.Api.Tests/AdminConsoleDataVolumeEndpointTests.cs Iverson.Server/Iverson.Api.Tests/Helpers/AdminConsoleTestWebApplicationFactory.cs Iverson.Server/Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs Iverson.Server/Iverson.StarRocks.Tests/MatchRowsIntegrationTests.cs
git commit -m "stream tenant-scoped StarRocks rows for MatchPattern"
```

### Task 4: `ScrollAsync` and the two-phase CHUNKS source

**Files:**
- Create: `Iverson.Server/Iverson.Vector/VectorScrollPage.cs`, `Iverson.Server/Iverson.Vector/IChunkRowSource.cs`, `Iverson.Server/Iverson.Vector/QdrantChunkRowSource.cs`
- Modify: `Iverson.Server/Iverson.Vector/IVectorRoles.cs:41-44` (`RetrieveNamedVectorAsync` gains an optional token; `ScrollAsync` is added)
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs:156-188` (`RetrieveNamedVectorAsync` passes the token; `ScrollAsync` is implemented)
- Test: `Iverson.Server/Iverson.Vector.Tests/ChunkRowSourceIntegrationTests.cs`

**Interfaces:**
- Consumes: `PatternBudgetExceededException` from `Iverson.Patterns`, through the Task 1 reference.
- Produces:
  - `IVectorQueryService.ScrollAsync` and the tokened `RetrieveNamedVectorAsync`, for Task 5
  - `IChunkRowSource`, `ChunkRow`, `ChunkRowQuery` and `QdrantChunkRowSource`, for Task 7

**Deliberate deviation from spec §3.2's parameter list:**
- **The change:** `ScrollAsync` also takes the page `offset` and returns one page, instead of paging internally.
- **Why:** each Qdrant call needs its own scoped API key, a 30 s JWT, set through the `AsyncLocal` `RequestHeaders` scope (plan-level assumption 10). The chunk source can only wrap every page call in its own scope if it drives the paging itself.
- **What stays the same:** the rest of the spec's parameters, in the spec's order.

- [ ] **Step 1: Create the contract types**

`Iverson.Server/Iverson.Vector/VectorScrollPage.cs`:
```csharp
using Qdrant.Client.Grpc;

namespace Iverson.Vector;

/// <summary>A scrolled point: its payload canonicalised to strings (as <c>SearchNamedAsync</c> does) and, when a
/// vector was selected and the point has it, that vector.</summary>
public sealed record ScrolledPoint(ulong Id, IReadOnlyDictionary<string, string> Payload, float[]? Vector);

/// <summary>One scroll page; <see cref="NextOffset"/> is null after the last page.</summary>
public sealed record VectorScrollPage(IReadOnlyList<ScrolledPoint> Points, PointId? NextOffset);
```

`Iverson.Server/Iverson.Vector/IChunkRowSource.cs`:
```csharp
using Qdrant.Client.Grpc;

namespace Iverson.Vector;

/// <summary>One CHUNKS row (spec §1): the three chunk columns, plus the chunk's own vector when requested and
/// present (null otherwise — SIMILARITY then reads NULL).</summary>
public sealed record ChunkRow(string ParentKey, int ChunkIndex, string Text, float[]? Vector);

/// <param name="ChunksCollection">The tenant's resolved chunks collection.</param>
/// <param name="Filter">The caller's chunk filter (<c>BuildChunksFilter</c> + <c>ApplyOwnership</c>), or null.</param>
/// <param name="Field">The chunk field's canonical property name, matched against the payload <c>field</c>.</param>
/// <param name="VectorName">The chunk vector to return, or null when the request uses no SIMILARITY.</param>
/// <param name="BatchRows">Phase-2 batch bound, in chunks (spec §4).</param>
/// <param name="MaxRowsScanned">Phase-1 bound; more chunks than this throws <c>PatternBudgetExceededException</c>.</param>
public sealed record ChunkRowQuery(
    string ChunksCollection, Filter? Filter, string Field, string? VectorName, int BatchRows, int MaxRowsScanned);

/// <summary>The CHUNKS source for MatchPattern (spec §3.2).</summary>
public interface IChunkRowSource
{
    /// <summary>Streams the chunks ordered by parent key (ordinal), then chunk index (numeric). A missing
    /// collection yields nothing.</summary>
    IAsyncEnumerable<ChunkRow> ReadAsync(ChunkRowQuery query, CancellationToken ct = default);
}
```

In `IVectorRoles.cs`, change `RetrieveNamedVectorAsync` to:
```csharp
    Task<IReadOnlyDictionary<ulong, float[]>> RetrieveNamedVectorAsync(
        string collectionName,
        IReadOnlyList<ulong> ids,
        string vectorName,
        CancellationToken ct = default);
```
Add this after it:
```csharp
    /// <summary>
    /// One page of the points matching <paramref name="filter"/>, returning only <paramref name="payloadFields"/>
    /// and, when <paramref name="vectorName"/> is set, that named vector. Pass the previous page's
    /// <see cref="VectorScrollPage.NextOffset"/> as <paramref name="offset"/>; null starts at the beginning.
    /// </summary>
    Task<VectorScrollPage> ScrollAsync(
        string collectionName,
        Filter? filter,
        IReadOnlyList<string> payloadFields,
        string? vectorName,
        uint pageSize,
        PointId? offset = null,
        CancellationToken ct = default);
```

- [ ] **Step 2: Write the failing integration tests**

`Iverson.Server/Iverson.Vector.Tests/ChunkRowSourceIntegrationTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Patterns;
using NSubstitute;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Vector.Tests;

[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class ChunkRowSourceIntegrationTests(QdrantContainerFixture fixture) : IClassFixture<QdrantContainerFixture>
{
    private readonly IntelligenceVectorService _svc = fixture.Service;
    private readonly IntelligenceCollectionManager _mgr = fixture.CollectionManager;
    private readonly IntelligenceTenantScope _scope = new("test-signing-key-0123456789abcdef");
    private ulong _nextId = 1;

    private async Task<string> CollectionAsync(string vectorName = "body_vector")
    {
        var name = "ck_" + Guid.NewGuid().ToString("N")[..8];
        await _mgr.ApplyCollectionAsync(new CollectionSchema(name, [new NamedVector(vectorName, 4)], []));
        return name;
    }

    private Task AddChunkAsync(string collection, string parent, int index, string field = "Body",
        string owner = "u1", string vectorName = "body_vector", float first = 1f) =>
        _svc.UpsertNamedAsync(collection, _nextId++,
            new Dictionary<string, float[]> { [vectorName] = [first, 0f, 0f, index] },
            new Dictionary<string, object>
            {
                ["text"] = $"{parent}#{index}", ["parent_id"] = parent, ["field"] = field,
                ["chunk_index"] = index.ToString(), ["ownerId"] = owner,
            });

    private static async Task<List<ChunkRow>> ReadAllAsync(IChunkRowSource source, ChunkRowQuery query)
    {
        var rows = new List<ChunkRow>();
        await foreach (var row in source.ReadAsync(query)) rows.Add(row);
        return rows;
    }

    private QdrantChunkRowSource Source(IVectorQueryService? vector = null) => new(vector ?? _svc, _scope);

    private static ChunkRowQuery Query(string collection, string? vectorName = null, Filter? filter = null,
        int batchRows = 2_000, int maxRowsScanned = 100_000) =>
        new(collection, filter, "Body", vectorName, batchRows, maxRowsScanned);

    [Fact]
    public async Task Chunks_arrive_grouped_by_parent_in_ordinal_order_and_by_numeric_chunk_index()
    {
        var col = await CollectionAsync();
        foreach (var (parent, index) in new[] { ("b", 0), ("a", 10), ("B", 0), ("a", 2), ("a", 0), ("a", 1) })
            await AddChunkAsync(col, parent, index);

        var rows = await ReadAllAsync(Source(), Query(col));

        rows.Select(r => (r.ParentKey, r.ChunkIndex)).Should().Equal(
            ("B", 0), ("a", 0), ("a", 1), ("a", 2), ("a", 10), ("b", 0));
        rows[1].Text.Should().Be("a#0");
    }

    [Fact]
    public async Task Only_the_requested_field_and_the_callers_filter_are_read()
    {
        var col = await CollectionAsync();
        await AddChunkAsync(col, "a", 0);
        await AddChunkAsync(col, "a", 1, field: "Title");
        await AddChunkAsync(col, "b", 0, owner: "u2");

        var ownership = IntelligenceFilterBuilder.ApplyOwnership(null, ownershipRequired: true, "ownerId", "u1");
        var rows = await ReadAllAsync(Source(), Query(col, filter: ownership));

        rows.Select(r => (r.ParentKey, r.ChunkIndex)).Should().Equal(("a", 0));
    }

    [Fact]
    public async Task Vectors_come_back_only_when_requested()
    {
        var col = await CollectionAsync();
        await AddChunkAsync(col, "a", 3, first: 0.5f);

        (await ReadAllAsync(Source(), Query(col))).Single().Vector.Should().BeNull();
        (await ReadAllAsync(Source(), Query(col, vectorName: "body_vector"))).Single().Vector
            .Should().Equal(0.5f, 0f, 0f, 3f);
    }

    [Fact]
    public async Task A_missing_collection_yields_no_rows()
    {
        var rows = await ReadAllAsync(Source(), Query("ck_missing_" + Guid.NewGuid().ToString("N")[..8], vectorName: "body_vector"));

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_collection_without_the_vector_re_issues_the_scroll_without_it()
    {
        var col = await CollectionAsync(vectorName: "other_vector");
        await AddChunkAsync(col, "a", 0, vectorName: "other_vector");

        var rows = await ReadAllAsync(Source(), Query(col, vectorName: "body_vector"));

        rows.Should().ContainSingle().Which.Vector.Should().BeNull();
    }

    [Fact]
    public async Task Phase_one_over_MaxRowsScanned_raises_the_budget_exception()
    {
        var col = await CollectionAsync();
        for (var i = 0; i < 5; i++) await AddChunkAsync(col, "a", i);

        var act = () => ReadAllAsync(Source(), Query(col, maxRowsScanned: 4));

        (await act.Should().ThrowAsync<PatternBudgetExceededException>()).Which.BudgetName.Should().Be("MaxRowsScanned");
        (await ReadAllAsync(Source(), Query(col, maxRowsScanned: 5))).Should().HaveCount(5);
    }

    [Theory]
    [InlineData(3, 2)]   // [a(2)] then [b(2) c(1)]
    [InlineData(2, 3)]   // [a] [b] [c]
    [InlineData(1, 3)]   // a parent larger than BatchRows forms a batch on its own
    public async Task Phase_two_batches_whole_parents_by_BatchRows(int batchRows, int expectedPhaseTwoScrolls)
    {
        var col = await CollectionAsync();
        foreach (var (parent, index) in new[] { ("a", 0), ("a", 1), ("b", 0), ("b", 1), ("c", 0) })
            await AddChunkAsync(col, parent, index);

        var phaseTwo = 0;
        var spy = Substitute.For<IVectorQueryService>();
        spy.ScrollAsync(default!, default, default!, default, default, default, default).ReturnsForAnyArgs(ci =>
        {
            if (ci.ArgAt<IReadOnlyList<string>>(2).Contains("text")) phaseTwo++;
            return _svc.ScrollAsync(ci.ArgAt<string>(0), ci.ArgAt<Filter?>(1), ci.ArgAt<IReadOnlyList<string>>(2),
                ci.ArgAt<string?>(3), ci.ArgAt<uint>(4), ci.ArgAt<PointId?>(5), ci.ArgAt<CancellationToken>(6));
        });

        var rows = await ReadAllAsync(Source(spy), Query(col, batchRows: batchRows));

        rows.Should().HaveCount(5);
        phaseTwo.Should().Be(expectedPhaseTwoScrolls);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests --filter "FullyQualifiedName~ChunkRowSourceIntegrationTests"`
Expected: build FAIL. `QdrantChunkRowSource` does not exist, and `IntelligenceVectorService` does not implement `ScrollAsync`.

- [ ] **Step 4: Implement `ScrollAsync` and the token on `RetrieveNamedVectorAsync`**

In `IntelligenceVectorService.RetrieveNamedVectorAsync`:
- add the `CancellationToken ct = default` parameter;
- pass `cancellationToken: ct` to its `client.RetrieveAsync` call.

The list overload of `RetrieveAsync` takes a trailing `CancellationToken cancellationToken` (1.18.1, decompiled). Change nothing else. The existing caller, `RetrieveVectorsOrDegradeAsync`, passes no token and keeps its behaviour (plan-level assumption 22).

Add `ScrollAsync` after `RetrieveNamedVectorAsync`. Follow the telemetry shape of the neighbouring methods: `Telemetry.Source.StartActivity("qdrant.scroll", ActivityKind.Client)`, with the `db.system` and `qdrant.collection` tags and `SetStatus(Ok)`. Core:
```csharp
        WithVectorsSelector vectors = vectorName is null ? false : new[] { vectorName };
        var response = await client.ScrollAsync(
            collectionName, filter, pageSize, offset,
            payloadSelector: payloadFields.ToArray(), vectorsSelector: vectors, cancellationToken: ct);

        var points = new List<ScrolledPoint>(response.Result.Count);
        foreach (var p in response.Result)
        {
            float[]? vector = null;
            if (vectorName is not null && p.Vectors?.Vectors?.Vectors.TryGetValue(vectorName, out var v) == true)
            {
                var data = v.Dense?.Data ?? v.Data;      // 1.18 exposes both; read whichever is set
                if (data is { Count: > 0 }) vector = data.ToArray();
            }
            points.Add(new ScrolledPoint(
                p.Id.Num, p.Payload.ToDictionary(kvp => kvp.Key, kvp => ToCanonicalString(kvp.Value)), vector));
        }
        return new VectorScrollPage(points, response.NextPageOffset);
```

- [ ] **Step 5: Implement `QdrantChunkRowSource`**

`Iverson.Server/Iverson.Vector/QdrantChunkRowSource.cs` is a `public sealed class QdrantChunkRowSource(IVectorQueryService vector, IntelligenceTenantScope tenantScope) : IChunkRowSource`.

Page sizes:
- `ParentPageSize = 1024u`: phase 1 reads only the `parent_id` payload.
- `ChunkPageSize = 256u`: 256 × (768 floats + a chunk's text) stays well under `Grpc.Net.Client`'s 4 MB message cap. `RetrieveNamedVectorAsync` uses 512 vectors of 768 floats, about 1.6 MB, for the same reason.

`ReadAsync(query, [EnumeratorCancellation] ct)` works as follows.

1. **One Qdrant call per scope.** Every scroll goes through one private helper that wraps only that call:
   ```csharp
   private async Task<VectorScrollPage> PageAsync(string collection, Filter filter, string[] payload, string? vectorName,
       uint pageSize, PointId? offset, CancellationToken ct)
   {
       using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collection, readOnly: true)))
           return await vector.ScrollAsync(collection, filter, payload, vectorName, pageSize, offset, ct);
   }
   ```
   A scope never spans a `yield` (plan-level assumption 10).
2. **Base filter.** Start from `query.Filter?.Clone() ?? new Filter()` and add `Conditions.MatchKeyword("field", query.Field)` to `Must`.
3. **Phase 1.**
   - Page through the base filter with `["parent_id"]`, no vector and `ParentPageSize`, until `NextOffset` is null.
   - Count the chunks per `parent_id` in a `Dictionary<string, int>`.
   - As soon as the running total exceeds `query.MaxRowsScanned`, throw `new PatternBudgetExceededException("MaxRowsScanned")`.
   - An `RpcException` with `StatusCode.NotFound` from the first page means the collection does not exist: `yield break` (spec §3.2). Catch it outside any `yield`.
   - Sort the parent keys with `StringComparer.Ordinal`.
4. **Batches.** Walk the sorted parents, accumulating their counts. Close the current batch when adding the next parent would exceed `query.BatchRows` and the batch is non-empty. A parent larger than `BatchRows` therefore forms a batch on its own.
5. **Phase 2, per batch.**
   - Filter: the base filter plus `Conditions.Match("parent_id", batch)`.
   - Payload: `["text", "parent_id", "chunk_index"]`.
   - Vector: `query.VectorName`, unless a previous batch found it missing.
   - Page with `ChunkPageSize` until `NextOffset` is null, and collect the batch's points.
   - If a page throws `RpcException` with `StatusCode.InvalidArgument` and the status detail contains `"Not existing vector name"` (spec §3.3), set `vectorMissing = true` and restart this batch's paging from `offset: null` without the vector. Every later row's vector is then null.
6. **Order and yield.**
   - Group the batch's points by `parent_id`, in ordinal key order.
   - Within a parent, sort by `int.Parse(payload["chunk_index"], CultureInfo.InvariantCulture)`.
   - Yield `new ChunkRow(parent, index, payload["text"], point.Vector)`.
   - Call `ct.ThrowIfCancellationRequested()` before each yield.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests --filter "FullyQualifiedName~ChunkRowSourceIntegrationTests"`
Expected: PASS (6 facts, plus the 3-case theory).

Run: `dotnet test Iverson.Server/Iverson.Vector.Tests --filter "Category!=Integration"`
Expected: PASS, including the implements-contract test at `QdrantVectorServiceTests.cs:251`.

Run: `dotnet build Iverson.slnx`
Expected: `0 Error(s)`.

- [ ] **Step 7: Commit**
```bash
git add Iverson.Server/Iverson.Vector/VectorScrollPage.cs Iverson.Server/Iverson.Vector/IChunkRowSource.cs Iverson.Server/Iverson.Vector/QdrantChunkRowSource.cs Iverson.Server/Iverson.Vector/IVectorRoles.cs Iverson.Server/Iverson.Vector/IntelligenceVectorService.cs Iverson.Server/Iverson.Vector.Tests/ChunkRowSourceIntegrationTests.cs
git commit -m "add a paged Qdrant scroll and the two-phase chunk row source"
```

### Task 5: `SimilarityResolver`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/SimilarityResolver.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SimilarityResolverIntegrationTests.cs`

**Interfaces:**
- Consumes:
  - `IVectorQueryService.RetrieveNamedVectorAsync(…, ct)` (Task 4)
  - `SimilarityTerm` from `Iverson.Patterns`
  - `IEmbeddingService`, `IntelligenceTenantScope`, `Qdrant.Client.RequestHeaders`
- Produces: `SimilarityResolver`, consumed by Task 7

- [ ] **Step 1: Write the failing tests**

`Iverson.Server/Iverson.Api.Tests/Grpc/SimilarityResolverIntegrationTests.cs`:
```csharp
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Embeddings;
using Iverson.Patterns;
using Iverson.Vector;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class SimilarityResolverIntegrationTests(QdrantGrpcContainerFixture fixture) : IClassFixture<QdrantGrpcContainerFixture>
{
    private readonly IntelligenceVectorService _vector = fixture.Service;
    private readonly IntelligenceCollectionManager _mgr = fixture.CollectionManager;
    private readonly IntelligenceTenantScope _scope = new("test-signing-key-0123456789abcdef");

    private async Task<string> CollectionWithAsync(string vectorName, params (ulong Id, float[] Vector)[] points)
    {
        var name = "sim_" + Guid.NewGuid().ToString("N")[..8];
        await _mgr.ApplyCollectionAsync(new CollectionSchema(name, [new NamedVector(vectorName, 4)], []));
        foreach (var (id, v) in points)
            await _vector.UpsertNamedAsync(name, id, new Dictionary<string, float[]> { [vectorName] = v });
        return name;
    }

    private static readonly float[] Query = [1f, 0f, 0f, 0f];

    [Fact]
    public async Task Scores_every_present_vector_and_leaves_an_absent_point_null()
    {
        var col = await CollectionWithAsync("title_vector", (1, [1f, 0f, 0f, 0f]), (2, [0f, 1f, 0f, 0f]));

        var scores = await new SimilarityResolver(_vector, _scope)
            .ScoreRowsAsync(col, [1, 2, 3], ["title_vector"], [Query], CancellationToken.None);

        scores[0][0].Should().BeApproximately(1.0, 1e-6);
        scores[1][0].Should().BeApproximately(0.0, 1e-6);
        scores[2][0].Should().BeNull();
    }

    [Fact]
    public async Task A_stored_zero_vector_scores_NaN()
    {
        var col = await CollectionWithAsync("title_vector", (1, [0f, 0f, 0f, 0f]));

        var scores = await new SimilarityResolver(_vector, _scope)
            .ScoreRowsAsync(col, [1], ["title_vector"], [Query], CancellationToken.None);

        scores[0][0].Should().Be(double.NaN);
    }

    [Fact]
    public async Task A_missing_object_collection_or_an_unconfigured_vector_scores_null()
    {
        var resolver = new SimilarityResolver(_vector, _scope);

        var missing = await resolver.ScoreRowsAsync("sim_missing_" + Guid.NewGuid().ToString("N")[..8],
            [1], ["title_vector"], [Query], CancellationToken.None);
        missing[0][0].Should().BeNull();

        var col = await CollectionWithAsync("other_vector", (1, [1f, 0f, 0f, 0f]));
        var unconfigured = await resolver.ScoreRowsAsync(col, [1], ["title_vector"], [Query], CancellationToken.None);
        unconfigured[0][0].Should().BeNull();
    }

    [Fact]
    public async Task Each_distinct_vector_name_is_retrieved_once_per_batch()
    {
        var col = await CollectionWithAsync("title_vector", (1, [1f, 0f, 0f, 0f]));
        var spy = Substitute.For<IVectorQueryService>();
        spy.RetrieveNamedVectorAsync(default!, default!, default!, default).ReturnsForAnyArgs(ci =>
            _vector.RetrieveNamedVectorAsync(ci.ArgAt<string>(0), ci.ArgAt<IReadOnlyList<ulong>>(1), ci.ArgAt<string>(2), ci.ArgAt<CancellationToken>(3)));

        var scores = await new SimilarityResolver(spy, _scope)
            .ScoreRowsAsync(col, [1, 1], ["title_vector", "title_vector"], [Query, [0f, 1f, 0f, 0f]], CancellationToken.None);

        await spy.Received(1).RetrieveNamedVectorAsync(col, Arg.Any<IReadOnlyList<ulong>>(), "title_vector", Arg.Any<CancellationToken>());
        scores[1][0].Should().BeApproximately(1.0, 1e-6);
        scores[1][1].Should().BeApproximately(0.0, 1e-6);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable, "down")]
    [InlineData(StatusCode.InvalidArgument, "Wrong input: some other problem")]
    public async Task Any_other_retrieve_failure_propagates(StatusCode code, string detail)
    {
        var failing = Substitute.For<IVectorQueryService>();
        failing.RetrieveNamedVectorAsync(default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new RpcException(new Status(code, detail)));

        var act = () => new SimilarityResolver(failing, _scope)
            .ScoreRowsAsync("col", [1], ["title_vector"], [Query], CancellationToken.None);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(code);
    }

    [Fact]
    public void Chunk_vectors_score_against_every_term_and_an_absent_vector_scores_null()
    {
        var scores = SimilarityResolver.ScoreVectors([[1f, 0f, 0f, 0f], null], [Query, [0f, 1f, 0f, 0f]]);

        scores[0][0].Should().BeApproximately(1.0, 1e-6);
        scores[0][1].Should().BeApproximately(0.0, 1e-6);
        scores[1].Should().Equal(null, null);
    }

    [Fact]
    public async Task Each_distinct_text_is_embedded_once()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedQueryAsync("refund", Arg.Any<CancellationToken>()).Returns([1f, 0f, 0f, 0f]);
        embedding.EmbedQueryAsync("cancel", Arg.Any<CancellationToken>()).Returns([0f, 1f, 0f, 0f]);

        var vectors = await SimilarityResolver.EmbedAsync(embedding,
            [new SimilarityTerm("title", "refund"), new SimilarityTerm("body", "refund"), new SimilarityTerm("title", "cancel")],
            CancellationToken.None);

        vectors.Select(v => v[0]).Should().Equal(1f, 1f, 0f);
        await embedding.Received(1).EmbedQueryAsync("refund", Arg.Any<CancellationToken>());
        await embedding.Received(1).EmbedQueryAsync("cancel", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~SimilarityResolverIntegrationTests"`
Expected: build FAIL, because `SimilarityResolver` does not exist.

- [ ] **Step 3: Implement the resolver**

`Iverson.Server/Iverson.Api/Grpc/SimilarityResolver.cs`, namespace `Iverson.Api.Grpc`:
```csharp
/// <summary>
/// SIMILARITY scores for MatchPattern (spec §3.3). Never uses RetrieveVectorsOrDegradeAsync: a retrieve failure fails
/// the request, except the two states that mean "no vector yet" — a missing object collection (NotFound) and a
/// collection that predates the vector (InvalidArgument "Not existing vector name") — which leave the batch's
/// scores NULL for that vector.
/// </summary>
internal sealed class SimilarityResolver(IVectorQueryService vector, IntelligenceTenantScope tenantScope)
{
    /// <summary>The query vector of each term, embedding each distinct text once.</summary>
    public static Task<float[][]> EmbedAsync(IEmbeddingService embedding, IReadOnlyList<SimilarityTerm> terms, CancellationToken ct);

    /// <summary>TYPE_ROWS: <c>scores[row][term]</c> for the batch's point ids; each distinct vector name is retrieved
    /// once, under a read-only scoped key; an absent vector is <c>null</c>.</summary>
    public Task<double?[][]> ScoreRowsAsync(string objectCollection, IReadOnlyList<ulong> pointIds,
        IReadOnlyList<string> termVectorNames, IReadOnlyList<float[]> termQueryVectors, CancellationToken ct);

    /// <summary>CHUNKS: <c>scores[row][term]</c> against each row's own chunk vector (null when absent).</summary>
    public static double?[][] ScoreVectors(IReadOnlyList<float[]?> rowVectors, IReadOnlyList<float[]> termQueryVectors);
}
```
Implementation rules:
- **`EmbedAsync`**:
  - Keep a `Dictionary<string, float[]>` keyed ordinally by text.
  - For each term, call `await embedding.EmbedQueryAsync(term.Text, ct)` only for a text not already in the dictionary.
  - Return the vectors in term order.
  - Exceptions propagate. The RPC maps them (Task 7).
- **`ScoreRowsAsync`**:
  - For each distinct name in `termVectorNames`, retrieve the vectors once:
    - Pass the distinct `pointIds`.
    - Wrap only the call, as `using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(objectCollection, readOnly: true))) vectors = await vector.RetrieveNamedVectorAsync(objectCollection, ids, name, ct);`.
    - Catch `RpcException` when `ex.StatusCode == StatusCode.NotFound`, or when `ex.StatusCode == StatusCode.InvalidArgument && ex.Status.Detail.Contains("Not existing vector name", StringComparison.Ordinal)`. Either one means an empty map for that name.
    - Let any other exception propagate.
  - For row `r` and term `t`:
    - if the map for `termVectorNames[t]` has `pointIds[r]`, the score is `(double)TensorPrimitives.CosineSimilarity(termQueryVectors[t], v)`;
    - otherwise the score is `null`.
  - A zero-magnitude vector yields `NaN`, which is kept (spec §2).
- **`ScoreVectors`**: the same cosine for each row and term, and `null` for a null row vector.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~SimilarityResolverIntegrationTests"`
Expected: PASS (6 facts plus the 2-case theory). The Qdrant container starts on first use.

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/SimilarityResolver.cs Iverson.Server/Iverson.Api.Tests/Grpc/SimilarityResolverIntegrationTests.cs
git commit -m "add the MatchPattern similarity resolver"
```

### Task 6: `PatternPartitionBatcher` — whole partitions in bounded batches (no I/O)

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/PatternPartitionBatcher.cs`
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/PatternPartitionBatcherTests.cs`

**Interfaces:**
- Consumes: `PatternBudgetExceededException` from `Iverson.Patterns`.
- Produces: `PatternInputRow`, `PatternBatch` and `PatternPartitionBatcher.BatchAsync`, consumed by Task 7.

- [ ] **Step 1: Write the failing tests**

`Iverson.Server/Iverson.Api.Tests/Grpc/PatternPartitionBatcherTests.cs`:
```csharp
using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Patterns;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public sealed class PatternPartitionBatcherTests
{
    private static PatternInputRow Row(object? partition, int id) =>
        new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["p"] = partition, ["id"] = id }, null);

    /// <summary>Rows for consecutive partitions of the given sizes; partition i has key "k{i}".</summary>
    private static List<PatternInputRow> Partitions(params int[] sizes)
    {
        var rows = new List<PatternInputRow>();
        var id = 0;
        for (var i = 0; i < sizes.Length; i++)
            for (var j = 0; j < sizes[i]; j++) rows.Add(Row($"k{i}", id++));
        return rows;
    }

    private static async IAsyncEnumerable<PatternInputRow> Stream(IEnumerable<PatternInputRow> rows, Action? onEach = null)
    {
        foreach (var row in rows)
        {
            onEach?.Invoke();
            await Task.Yield();
            yield return row;
        }
    }

    private static async Task<List<PatternBatch>> BatchAllAsync(IEnumerable<PatternInputRow> rows,
        int batchRows = 2_000, int maxPartitionRows = 10_000, int? maxRowsScanned = null, string[]? partitionBy = null)
    {
        var batches = new List<PatternBatch>();
        await foreach (var b in PatternPartitionBatcher.BatchAsync(Stream(rows), partitionBy ?? ["p"], batchRows, maxPartitionRows, maxRowsScanned))
            batches.Add(b);
        return batches;
    }

    private static int[][] Shape(IEnumerable<PatternBatch> batches) =>
        batches.Select(b => b.Partitions.Select(p => p.Count).ToArray()).ToArray();

    [Fact]
    public async Task Partitions_are_never_split_and_batches_pack_to_BatchRows()
    {
        Shape(await BatchAllAsync(Partitions(2, 2, 3, 1), batchRows: 4))
            .Should().BeEquivalentTo(new[] { new[] { 2, 2 }, new[] { 3, 1 } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_partition_larger_than_BatchRows_forms_a_batch_on_its_own()
    {
        Shape(await BatchAllAsync(Partitions(1, 5, 1), batchRows: 3))
            .Should().BeEquivalentTo(new[] { new[] { 1 }, new[] { 5 }, new[] { 1 } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task No_partition_columns_makes_the_whole_input_one_partition()
    {
        var batches = await BatchAllAsync(Partitions(2, 3), batchRows: 2, partitionBy: []);

        Shape(batches).Should().BeEquivalentTo(new[] { new[] { 5 } });
    }

    [Fact]
    public async Task Null_partition_values_group_together_and_values_compare_by_value()
    {
        var rows = new[] { Row(null, 0), Row(null, 1), Row(1L, 2), Row(1L, 3), Row("1", 4) };

        Shape(await BatchAllAsync(rows, batchRows: 100))
            .Should().BeEquivalentTo(new[] { new[] { 2, 2, 1 } });
    }

    [Fact]
    public async Task Rows_keep_their_order_inside_each_partition()
    {
        var batches = await BatchAllAsync(Partitions(3, 2), batchRows: 100);

        batches.Single().Partitions.SelectMany(p => p).Select(r => r.Columns["id"]).Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public async Task One_partition_over_MaxPartitionRows_raises_the_budget_exception()
    {
        var act = () => BatchAllAsync(Partitions(1, 3), maxPartitionRows: 2);

        (await act.Should().ThrowAsync<PatternBudgetExceededException>()).Which.BudgetName.Should().Be("MaxPartitionRows");
    }

    [Fact]
    public async Task MaxRowsScanned_counts_every_row_and_trips_after_earlier_batches_were_yielded()
    {
        var yielded = new List<PatternBatch>();
        var act = async () =>
        {
            await foreach (var b in PatternPartitionBatcher.BatchAsync(Stream(Partitions(1, 1, 1, 1, 1)), ["p"], 1, 100, 4))
                yielded.Add(b);
        };

        (await act.Should().ThrowAsync<PatternBudgetExceededException>()).Which.BudgetName.Should().Be("MaxRowsScanned");
        yielded.Should().NotBeEmpty("a budget hit after output was written must still end the stream with an error");
    }

    [Fact]
    public async Task A_batch_is_yielded_before_the_source_is_exhausted()
    {
        var produced = 0;
        var source = Stream(Partitions(Enumerable.Repeat(1, 5_000).ToArray()), () => produced++);

        await foreach (var _ in PatternPartitionBatcher.BatchAsync(source, ["p"], 2_000, 10_000, null))
        {
            produced.Should().BeLessThan(5_000, "rows stream through; the batcher never buffers the whole source");
            break;
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~PatternPartitionBatcherTests"`
Expected: build FAIL, because `PatternPartitionBatcher` does not exist.

- [ ] **Step 3: Implement the batcher**

`Iverson.Server/Iverson.Api/Grpc/PatternPartitionBatcher.cs`, namespace `Iverson.Api.Grpc`:
```csharp
/// <summary>One engine input row and, for CHUNKS, the chunk's own vector (null otherwise).</summary>
internal sealed record PatternInputRow(IDictionary<string, object?> Columns, float[]? ChunkVector);

/// <summary>Whole partitions, each in partition order (spec §4).</summary>
internal sealed record PatternBatch(IReadOnlyList<IReadOnlyList<PatternInputRow>> Partitions)
{
    public int RowCount => Partitions.Sum(p => p.Count);
}

internal static class PatternPartitionBatcher
{
    /// <summary>
    /// Groups <paramref name="rows"/> (already ordered by partition) into whole partitions and packs them into batches
    /// of about <paramref name="batchRows"/> rows; a partition larger than that forms a batch on its own (spec §4).
    /// Throws <see cref="PatternBudgetExceededException"/> naming <c>MaxPartitionRows</c> when one partition exceeds
    /// <paramref name="maxPartitionRows"/>, and <c>MaxRowsScanned</c> when more than <paramref name="maxRowsScanned"/>
    /// rows arrive (null: the source enforces it, as the CHUNKS source does).
    /// </summary>
    public static IAsyncEnumerable<PatternBatch> BatchAsync(
        IAsyncEnumerable<PatternInputRow> rows, IReadOnlyList<string> partitionBy,
        int batchRows, int maxPartitionRows, int? maxRowsScanned, CancellationToken ct = default);
}
```
Rules. Implement `BatchAsync` as an `async` iterator whose `ct` parameter carries `[EnumeratorCancellation]`.
- Count every row. When the count exceeds `maxRowsScanned` (if set), throw `new PatternBudgetExceededException("MaxRowsScanned")`.
- A row starts a new partition when some `partitionBy` column has a different value from the current partition's first row. Compare with `object.Equals(a, b)`, so `null` equals `null` and boxed values compare by value. An empty `partitionBy` makes the whole input one partition.
- When the current partition's row count exceeds `maxPartitionRows`, throw `new PatternBudgetExceededException("MaxPartitionRows")`.
- Adding a completed partition:
  1. If the pending batch is non-empty and adding the partition would take it above `batchRows`, yield the pending batch first.
  2. Add the partition.
  3. If the batch now holds `batchRows` rows or more, yield it.
- At the end, add the last partition and yield any non-empty pending batch.
- Never buffer more than the pending batch plus the current partition.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~PatternPartitionBatcherTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/PatternPartitionBatcher.cs Iverson.Server/Iverson.Api.Tests/Grpc/PatternPartitionBatcherTests.cs
git commit -m "batch MatchPattern input rows into whole partitions"
```

### Task 7: `ObjectSearchGrpcService.MatchPattern` and `PatternQueryLimitOptions`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/PatternQueryLimitOptions.cs`, `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.MatchPattern.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:213` (the primary constructor's last parameter line; nothing else)
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (register `PatternQueryLimitOptions` after the `builder.Services.AddQdrant(...)` call)
- Modify: `Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs:210-211` (the doc comment that lists the dictionary overload's callers)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternGrpcServiceTests.cs`

**Interfaces:**
- Consumes:
  - the proto types (Task 1)
  - `MatchRowsRequest` and `MatchRowsAsync` (Tasks 2–3)
  - `IChunkRowSource`, `ChunkRowQuery` and `QdrantChunkRowSource` (Task 4)
  - `SimilarityResolver` (Task 5)
  - `PatternPartitionBatcher` (Task 6)
  - the engine: `PatternQuery.Compile`, `CompiledPattern`, `PatternBudget`, `MatchOutputRow`, the exceptions
- Produces: the working `MatchPattern` RPC, which Plan 3's SDKs call.

- [ ] **Step 1: Create the limits class and the constructor parameters**

`Iverson.Server/Iverson.Api/Grpc/PatternQueryLimitOptions.cs`:
```csharp
namespace Iverson.Api.Grpc;

/// <summary>
/// MatchPattern's limits (spec §5, §4). Built in Program.cs from individual <c>Patterns:Limits:*</c> reads, the same
/// way as <c>EngagementQueryLimitOptions</c>, and injected as a plain singleton.
/// </summary>
public sealed class PatternQueryLimitOptions
{
    public const string Section = "Patterns:Limits";

    /// <summary>Output rows when the request's <c>limit</c> is 0.</summary>
    public const int DefaultOutputRows = 1_000;

    public int MaxPatternLength { get; init; } = 1_000;
    public int MaxProgramInstructions { get; init; } = 5_000;
    public int MaxExpressionLength { get; init; } = 1_000;
    public int MaxDefines { get; init; } = 50;
    public int MaxMeasures { get; init; } = 50;
    public int MaxSubsets { get; init; } = 50;
    public int MaxSimilarityTerms { get; init; } = 10;
    public int MaxOutputRows { get; init; } = 10_000;
    public int MaxRowsScanned { get; init; } = 100_000;
    public int MaxPartitionRows { get; init; } = 10_000;
    public int MaxActiveThreads { get; init; } = 10_000;
    public long MaxSteps { get; init; } = 10_000_000;
    public int TimeoutSeconds { get; init; } = 30;
    public int BatchRows { get; init; } = 2_000;

    public static PatternQueryLimitOptions Default { get; } = new();
}
```

In `ObjectSearchGrpcService.cs:213`, change `    EngagementQueryLimitOptions queryLimits)` to:
```csharp
    EngagementQueryLimitOptions queryLimits,
    PatternQueryLimitOptions? patternLimits = null,
    IChunkRowSource? chunkRows = null)
```
Both new parameters are optional. That keeps all 16 existing test constructions compiling (plan-level assumption 23), and it lets DI construct the default `QdrantChunkRowSource` without a registration.

In `Program.cs`, directly after the `builder.Services.AddQdrant(...);` statement, add:
```csharp
// Spec §5: MatchPattern's limits, read one value at a time like EngagementQueryLimitOptions above.
builder.Services.AddSingleton(new PatternQueryLimitOptions
{
    MaxPatternLength       = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxPatternLength", PatternQueryLimitOptions.Default.MaxPatternLength),
    MaxProgramInstructions = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxProgramInstructions", PatternQueryLimitOptions.Default.MaxProgramInstructions),
    MaxExpressionLength    = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxExpressionLength", PatternQueryLimitOptions.Default.MaxExpressionLength),
    MaxDefines             = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxDefines", PatternQueryLimitOptions.Default.MaxDefines),
    MaxMeasures            = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxMeasures", PatternQueryLimitOptions.Default.MaxMeasures),
    MaxSubsets             = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxSubsets", PatternQueryLimitOptions.Default.MaxSubsets),
    MaxSimilarityTerms     = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxSimilarityTerms", PatternQueryLimitOptions.Default.MaxSimilarityTerms),
    MaxOutputRows          = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxOutputRows", PatternQueryLimitOptions.Default.MaxOutputRows),
    MaxRowsScanned         = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxRowsScanned", PatternQueryLimitOptions.Default.MaxRowsScanned),
    MaxPartitionRows       = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxPartitionRows", PatternQueryLimitOptions.Default.MaxPartitionRows),
    MaxActiveThreads       = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxActiveThreads", PatternQueryLimitOptions.Default.MaxActiveThreads),
    MaxSteps               = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxSteps", PatternQueryLimitOptions.Default.MaxSteps),
    TimeoutSeconds         = cfg.GetValue($"{PatternQueryLimitOptions.Section}:TimeoutSeconds", PatternQueryLimitOptions.Default.TimeoutSeconds),
    BatchRows              = cfg.GetValue($"{PatternQueryLimitOptions.Section}:BatchRows", PatternQueryLimitOptions.Default.BatchRows),
});
```
`Program.cs` already imports `Iverson.Api.Grpc` (`:11`).

In `AuthorizationFieldMasking.cs:210-211`, change "the three streaming SQL RPCs (Search, GroupBy, Pipeline)" to "the four streaming SQL RPCs (Search, GroupBy, Pipeline, MatchPattern)".

- [ ] **Step 2: Write the failing service tests**

`Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternGrpcServiceTests.cs`. The constructor setup mirrors `ObjectSearchGrpcServiceTests` (`:21-78`). The stores are NSubstitute fakes. The authorization evaluator, the schema registry and the tenant scope are real.
```csharp
using System.Diagnostics;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using ProtoRowsPerMatch = Iverson.Client.Contracts.RowsPerMatch;

namespace Iverson.Api.Tests.Grpc;

public sealed class MatchPatternGrpcServiceTests
{
    private const string SigningKey = "test-signing-key-0123456789abcdef";
    private readonly SchemaRegistry _registry;
    private readonly IEngagementStoreSearchService _search = Substitute.For<IEngagementStoreSearchService>();
    private readonly IVectorQueryService _vector = Substitute.For<IVectorQueryService>();
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly IEmbeddingServiceResolver _resolver = Substitute.For<IEmbeddingServiceResolver>();
    private readonly IChunkRowSource _chunkRows = Substitute.For<IChunkRowSource>();
    private readonly ActingUserAccessor _actingUserAccessor = new()
        { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
    private readonly IntelligenceTenantScope _tenantScope = new(SigningKey);

    public MatchPatternGrpcServiceTests()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry = new SchemaRegistry(new SchemaRegistryRepository(sql), NullLogger<SchemaRegistry>.Instance);
        _resolver.Get(Arg.Any<string?>()).Returns(_embedding);
    }

    private ObjectSearchGrpcService Sut(PatternQueryLimitOptions? limits = null) => new(
        _registry, _search, _vector, _resolver, NullLogger<ObjectSearchGrpcService>.Instance,
        _actingUserAccessor, new RowFieldAuthorizationEvaluator(), _tenantScope,
        new ResultReranker(Options.Create(new VectorRankingOptions())), new ResultDiversifier(),
        Options.Create(new VectorRankingOptions()), Options.Create(new DecayOptions()),
        Options.Create(new PopularitySignalOptions()), EngagementQueryLimitOptions.Default,
        limits ?? PatternQueryLimitOptions.Default, _chunkRows);

    private static (IServerStreamWriter<MatchPatternResponse> Writer, List<MatchPatternResponse> Written) MakeStream()
    {
        var written = new List<MatchPatternResponse>();
        var writer = Substitute.For<IServerStreamWriter<MatchPatternResponse>>();
        writer.WriteAsync(Arg.Do<MatchPatternResponse>(written.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return (writer, written);
    }

    private static IDictionary<string, object?> Row(params (string Key, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

    private static async IAsyncEnumerable<T> Async<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private void SourceRows(params IDictionary<string, object?>[] rows) =>
        _search.MatchRowsAsync(Arg.Any<EngagementQuerySchema>(), Arg.Any<MatchRowsRequest>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Async(rows));

    private static MatchPatternRequest Req(
        string pattern, (string Name, string Expr)[]? define = null, (string Name, string Expr)[]? measures = null,
        ProtoRowsPerMatch rows = ProtoRowsPerMatch.OneRow, string type = "Author", string[]? orderBy = null,
        string[]? partitionBy = null, int limit = 0, PatternRowSource source = PatternRowSource.TypeRows,
        string chunkProperty = "")
    {
        var r = new MatchPatternRequest
        {
            TypeName = type, Source = source, ChunkProperty = chunkProperty, Pattern = pattern,
            RowsPerMatch = rows, Limit = limit, TraceId = "t",
        };
        foreach (var o in orderBy ?? (source == PatternRowSource.TypeRows ? ["Id"] : [])) r.OrderBy.Add(new SearchSort { Property = o });
        foreach (var p in partitionBy ?? []) r.PartitionBy.Add(p);
        foreach (var (n, e) in define ?? []) r.Define.Add(new NamedExpr { Name = n, Expr = e });
        foreach (var (n, e) in measures ?? []) r.Measures.Add(new NamedExpr { Name = n, Expr = e });
        return r;
    }

    private async Task<List<MatchPatternResponse>> RunAsync(MatchPatternRequest request, PatternQueryLimitOptions? limits = null)
    {
        var (writer, written) = MakeStream();
        await Sut(limits).MatchPattern(request, writer, TestServerCallContext.Create());
        return written;
    }

    private async Task<StatusCode> StatusOfAsync(MatchPatternRequest request, PatternQueryLimitOptions? limits = null)
    {
        var act = () => RunAsync(request, limits);
        return (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode;
    }

    // ── happy paths ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Type_rows_all_rows_output_carries_columns_measures_match_number_classifier_and_trace_id()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        MatchRowsRequest? sent = null;
        _search.MatchRowsAsync(Arg.Any<EngagementQuerySchema>(), Arg.Do<MatchRowsRequest>(r => sent = r),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Async(new[]
            {
                Row(("Id", "1"), ("Name", "a"), ("Bio", "x")), Row(("Id", "2"), ("Name", "b"), ("Bio", "y")),
                Row(("Id", "3"), ("Name", "c"), ("Bio", "z")),
            }));

        var written = await RunAsync(Req("A B+", [("B", "B.Name > PREV(B.Name)")], [("m", "MATCH_NUMBER()")],
            ProtoRowsPerMatch.AllRowsShowEmpty));

        written.Select(w => w.Classifier).Should().Equal("A", "B", "B");
        written.Should().OnlyContain(w => w.MatchNumber == 1 && w.TraceId == "t");
        written[0].Data.Fields.Keys.Should().Equal("Id", "Name", "Bio", "m");
        written[1].Data.Fields["Name"].StringValue.Should().Be("b");
        sent!.OneRowPerMatch.Should().BeFalse();
        sent.ReferencedColumns.Should().BeEquivalentTo(["Name"]);
        sent.MeasureNames.Should().Equal("m");
        sent.MaxRowsScanned.Should().Be(PatternQueryLimitOptions.Default.MaxRowsScanned);
        sent.OrderBy.Select(o => o.Property).Should().Equal("Id");
    }

    [Fact]
    public async Task One_row_emits_the_partition_column_under_the_rows_spelling_then_the_measures()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a")), Row(("Id", "2"), ("Name", "a")), Row(("Id", "3"), ("Name", "b")));

        var written = await RunAsync(Req("A+", measures: [("n", "COUNT(*)")], partitionBy: ["name"]));

        written.Should().HaveCount(2);
        written[0].Data.Fields.Keys.Should().Equal("Name", "n");
        written[0].Data.Fields["n"].NumberValue.Should().Be(2);
        written[0].Classifier.Should().BeEmpty();
    }

    [Fact]
    public async Task The_tenant_column_is_stripped_from_every_output_row()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a"), (SchemaDescriptor.TenantColumnName, "test-tenant")));

        var written = await RunAsync(Req("A", rows: ProtoRowsPerMatch.AllRowsShowEmpty));

        written.Single().Data.Fields.Keys.Should().NotContain(SchemaDescriptor.TenantColumnName);
    }

    [Fact]
    public async Task Limit_stops_the_stream_and_zero_means_the_default()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Enumerable.Range(1, 5).Select(i => Row(("Id", $"{i}"), ("Name", "a"))).ToArray());

        (await RunAsync(Req("A", limit: 3))).Should().HaveCount(3);
        (await RunAsync(Req("A", limit: 0))).Should().HaveCount(5);
    }

    [Fact]
    public async Task Null_values_flow_through_as_sql_null()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a"), ("Bio", null)));

        var all = await RunAsync(Req("A", [("A", "A.Bio IS NULL")],
            [("c", "COALESCE(A.Bio, 'none')"), ("f", "FIRST(A.Bio)")], ProtoRowsPerMatch.AllRowsShowEmpty));
        all.Single().Data.Fields["Bio"].KindCase.Should().Be(Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue);
        all.Single().Data.Fields["c"].StringValue.Should().Be("none");
        all.Single().Data.Fields["f"].KindCase.Should().Be(Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue);

        var one = await RunAsync(Req("A", partitionBy: ["Bio"]));
        one.Single().Data.Fields["Bio"].KindCase.Should().Be(Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue);
    }

    [Fact]
    public async Task Type_rows_similarity_scores_reach_the_engine_from_the_callers_tenant_collection()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var (id1, id2) = ("0190a1b2-0000-7000-8000-000000000001", "0190a1b2-0000-7000-8000-000000000002");
        SourceRows(Row(("Id", id1), ("Title", "refund policy")), Row(("Id", id2), ("Title", "shipping")));
        _embedding.EmbedQueryAsync("refund", Arg.Any<CancellationToken>()).Returns([1f, 0f, 0f, 0f]);
        var collection = _tenantScope.ResolveCollectionName("articles", "test-tenant", isChunks: false);
        _vector.RetrieveNamedVectorAsync(collection, Arg.Any<IReadOnlyList<ulong>>(), "title_vector", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<ulong, float[]>
            {
                [IntelligenceStoreConsumer.KeyToUlong(id1)] = [1f, 0f, 0f, 0f],
                [IntelligenceStoreConsumer.KeyToUlong(id2)] = [0f, 1f, 0f, 0f],
            });

        var written = await RunAsync(Req("A", [("A", "SIMILARITY(title, 'refund') > 0.5")],
            [("s", "SIMILARITY(Title, 'refund')")], type: "Article"));

        written.Should().ContainSingle().Which.Data.Fields["s"].NumberValue.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public async Task Chunks_feed_parent_key_partitions_and_chunk_vectors_from_the_callers_chunks_collection()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        ChunkRowQuery? query = null;
        _chunkRows.ReadAsync(Arg.Do<ChunkRowQuery>(q => query = q), Arg.Any<CancellationToken>()).Returns(_ => Async(new[]
        {
            new ChunkRow("p1", 0, "intro", [0f, 1f, 0f, 0f]), new ChunkRow("p1", 1, "refunds", [1f, 0f, 0f, 0f]),
            new ChunkRow("p2", 0, "refunds", [1f, 0f, 0f, 0f]),
        }));
        _embedding.EmbedQueryAsync("refund", Arg.Any<CancellationToken>()).Returns([1f, 0f, 0f, 0f]);

        var written = await RunAsync(Req("A* B", [("B", "SIMILARITY(text, 'refund') > 0.9")], [("n", "COUNT(*)")],
            type: "Article", source: PatternRowSource.Chunks, chunkProperty: "body"));

        written.Select(w => (w.Data.Fields["parent_key"].StringValue, w.Data.Fields["n"].NumberValue))
            .Should().Equal(("p1", 2.0), ("p2", 1.0));
        query!.ChunksCollection.Should().Be(_tenantScope.ResolveCollectionName("articles", "test-tenant", isChunks: true));
        query.Field.Should().Be("Body");
        query.VectorName.Should().Be("body_vector");
        query.BatchRows.Should().Be(PatternQueryLimitOptions.Default.BatchRows);
        query.MaxRowsScanned.Should().Be(PatternQueryLimitOptions.Default.MaxRowsScanned);
    }

    [Fact]
    public async Task A_re_cased_chunk_property_is_accepted_for_a_field_restricted_caller_allowed_the_property()
    {
        var restricted = SchemaFixtures.ArticleSchema() with
        {
            Authorization = new AuthorizationRules(null, [new RowPermission("test-bypass", true, true, true)],
                [new FieldPermission("Title", ["admin"], [])]),
        };
        await _registry.RegisterAsync(restricted);
        _chunkRows.ReadAsync(Arg.Any<ChunkRowQuery>(), Arg.Any<CancellationToken>()).Returns(_ => Async(new[] { new ChunkRow("p1", 0, "x", null) }));

        var written = await RunAsync(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "BODY"));

        written.Should().ContainSingle();
    }

    [Fact]
    public async Task Tenant_isolation_the_callers_tenant_scopes_the_row_read()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null;
        _search.MatchRowsAsync(Arg.Any<EngagementQuerySchema>(), Arg.Any<MatchRowsRequest>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>>(a => authz = a), Arg.Any<CancellationToken>())
            .Returns(_ => Async(Array.Empty<IDictionary<string, object?>>()));

        await RunAsync(Req("A"));

        authz!["Author"].TenantValue.Should().Be("test-tenant");
    }

    [Fact]
    public async Task A_1000_character_nested_pattern_and_expression_run_on_a_thread_pool_thread()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a")));
        var pattern = new string('(', 499) + "A" + new string(')', 499);                    // 999 characters
        var define = new string('(', 498) + "TRUE" + new string(')', 498);                  // 1,000 characters

        var written = await Task.Run(() => RunAsync(Req(pattern, [("A", define)])));

        written.Should().ContainSingle();
    }

    // ── §6 failure semantics ────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_type_is_FailedPrecondition() =>
        (await StatusOfAsync(Req("A", type: "Nope"))).Should().Be(StatusCode.FailedPrecondition);

    [Fact]
    public async Task A_denied_caller_gets_an_empty_stream_and_no_store_is_read()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _actingUserAccessor.ActingUser = null;

        (await RunAsync(Req("A", [("A", "SIMILARITY(title, 'q') > 0")], type: "Article"))).Should().BeEmpty();
        (await RunAsync(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body"))).Should().BeEmpty();

        _search.ReceivedCalls().Should().BeEmpty();
        _chunkRows.ReceivedCalls().Should().BeEmpty();
        _embedding.ReceivedCalls().Should().BeEmpty();
    }

    public static TheoryData<string> LimitViolations() => new()
    {
        "pattern", "expression", "defines", "measures", "subsets", "where", "limit-high", "limit-negative",
        "type-rows-no-order-by", "chunks-partition-by", "chunks-order-by", "chunks-or-filter",
    };

    [Theory]
    [MemberData(nameof(LimitViolations))]
    public async Task A_limit_or_shape_violation_is_InvalidArgument_before_any_store_call(string violation)
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var limits = new PatternQueryLimitOptions
        {
            MaxPatternLength = 10, MaxExpressionLength = 10, MaxDefines = 1, MaxMeasures = 1, MaxSubsets = 1, MaxOutputRows = 5,
        };
        var clause = new SearchClause { Property = "Title", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "x" } };
        var request = violation switch
        {
            "pattern" => Req("A B C D E F", type: "Article"),
            "expression" => Req("A", [("A", "A.Title = 'abcdefgh'")], type: "Article"),
            "defines" => Req("A B", [("A", "TRUE"), ("B", "TRUE")], type: "Article"),
            "measures" => Req("A", measures: [("m", "1"), ("n", "2")], type: "Article"),
            "subsets" => WithSubsets(Req("A B", type: "Article")),
            "where" => WithWhere(Req("A", type: "Article"), Enumerable.Repeat(clause, EngagementQueryLimitOptions.Default.MaxClauses + 1)),
            "limit-high" => Req("A", type: "Article", limit: 6),
            "limit-negative" => Req("A", type: "Article", limit: -1),
            "type-rows-no-order-by" => Req("A", type: "Article", orderBy: []),
            "chunks-partition-by" => Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body", partitionBy: ["Title"]),
            "chunks-order-by" => Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body", orderBy: ["Title"]),
            _ => WithWhere(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Body"), [clause, clause], SearchLogic.Or),
        };

        (await StatusOfAsync(request, limits)).Should().Be(StatusCode.InvalidArgument);
        _search.ReceivedCalls().Should().BeEmpty();
        _chunkRows.ReceivedCalls().Should().BeEmpty();
    }

    private static MatchPatternRequest WithSubsets(MatchPatternRequest r)
    {
        r.Subsets.Add(new PatternSubset { Name = "U", Variables = { "A" } });
        r.Subsets.Add(new PatternSubset { Name = "V", Variables = { "B" } });
        return r;
    }

    private static MatchPatternRequest WithWhere(MatchPatternRequest r, IEnumerable<SearchClause> clauses, SearchLogic logic = SearchLogic.And)
    {
        r.Where.AddRange(clauses);
        r.WhereLogic = logic;
        return r;
    }

    [Theory]
    [InlineData("A |", null)]                           // pattern grammar
    [InlineData("A", "A.Title +")]                      // expression grammar
    [InlineData("A{5000}", null)]                       // MaxProgramInstructions (default 5,000)
    [InlineData("A", "SIMILARITY(Body, 'q') > 0")]      // Body has no [IversonEmbedding]
    public async Task Pattern_expression_and_similarity_validation_is_InvalidArgument_before_any_store_call(string pattern, string? define)
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        (await StatusOfAsync(Req(pattern, define is null ? null : [("A", define)], type: "Article")))
            .Should().Be(StatusCode.InvalidArgument);
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task More_distinct_similarity_terms_than_MaxSimilarityTerms_is_InvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        (await StatusOfAsync(Req("A", [("A", "SIMILARITY(Title, 'a') > SIMILARITY(Title, 'b')")], type: "Article"),
            new PatternQueryLimitOptions { MaxSimilarityTerms = 1 })).Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task A_similarity_column_the_caller_may_not_read_is_InvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with
        {
            Authorization = new AuthorizationRules(null, [new RowPermission("test-bypass", true, true, true)],
                [new FieldPermission("Title", ["admin"], [])]),
        });

        (await StatusOfAsync(Req("A", [("A", "SIMILARITY(title, 'q') > 0")], type: "Article")))
            .Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task A_bad_chunk_property_is_InvalidArgument_and_a_type_without_a_collection_is_FailedPrecondition()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        (await StatusOfAsync(Req("A", type: "Article", source: PatternRowSource.Chunks, chunkProperty: "Title")))
            .Should().Be(StatusCode.InvalidArgument);

        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { TypeName = "Bare", TableName = "bares", CollectionName = null });
        (await StatusOfAsync(Req("A", type: "Bare", source: PatternRowSource.Chunks, chunkProperty: "Body")))
            .Should().Be(StatusCode.FailedPrecondition);
        (await StatusOfAsync(Req("A", [("A", "SIMILARITY(Title, 'q') > 0")], type: "Bare")))
            .Should().Be(StatusCode.FailedPrecondition);
    }

    public static TheoryData<string, StatusCode> StoreFailures() => new()
    {
        { "translation", StatusCode.InvalidArgument },
        { "not-ready", StatusCode.Unavailable },
        { "disabled", StatusCode.FailedPrecondition },
        { "budget", StatusCode.ResourceExhausted },
    };

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public async Task Store_exceptions_map_as_the_spec_section_6_table_says(string failure, StatusCode expected)
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        Exception ex = failure switch
        {
            "translation" => new EngagementQueryTranslationException("unknown column 'x'"),
            "not-ready" => new EngagementNotReadyException("warming up"),
            "disabled" => new EngagementStoreDisabledException("off"),
            _ => new Iverson.Patterns.PatternBudgetExceededException("MaxRowsScanned"),
        };
        _search.MatchRowsAsync(default!, default!, default!, default).ThrowsForAnyArgs(ex);

        (await StatusOfAsync(Req("A"))).Should().Be(expected);
    }

    [Fact]
    public async Task Any_other_store_exception_propagates_unchanged()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _search.MatchRowsAsync(default!, default!, default!, default).ThrowsForAnyArgs(new InvalidOperationException("boom"));

        var act = () => RunAsync(Req("A"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task A_run_time_evaluation_error_is_InvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a")));

        (await StatusOfAsync(Req("A", [("A", "A.Name < 1")]))).Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task A_budget_hit_after_rows_were_written_still_ends_the_stream_with_an_error()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Row(("Id", "1"), ("Name", "a")), Row(("Id", "2"), ("Name", "b")), Row(("Id", "3"), ("Name", "c")));
        var (writer, written) = MakeStream();

        var act = () => Sut(new PatternQueryLimitOptions { MaxRowsScanned = 2, BatchRows = 1 })
            .MatchPattern(Req("A", partitionBy: ["Name"]), writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
        written.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("unavailable", StatusCode.Unavailable)]
    [InlineData("empty", StatusCode.InvalidArgument)]
    public async Task Embedding_failures_map_as_SearchChunks_does(string failure, StatusCode expected)
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedQueryAsync(default!, default).ThrowsAsyncForAnyArgs(failure == "empty"
            ? new EmptyEmbeddingInputException("empty")
            : new HttpRequestException("down"));

        (await StatusOfAsync(Req("A", [("A", "SIMILARITY(Title, 'q') > 0")], type: "Article"))).Should().Be(expected);
    }

    [Fact]
    public async Task Any_exception_raised_after_the_timeout_token_fires_is_DeadlineExceeded()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _search.MatchRowsAsync(default!, default!, default!, default).ReturnsForAnyArgs(ci => WaitThenFail(ci.ArgAt<CancellationToken>(3)));

        (await StatusOfAsync(Req("A"), new PatternQueryLimitOptions { TimeoutSeconds = 1 })).Should().Be(StatusCode.DeadlineExceeded);

        static async IAsyncEnumerable<IDictionary<string, object?>> WaitThenFail(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested)
                throw new InvalidOperationException("a cancelled StarRocks read surfaces as some other exception (spec §3.2)");
            yield break;
        }
    }

    [Fact]
    public async Task The_timeout_stops_an_output_heavy_run_within_about_one_output_row()
    {
        // CDR-8 P6's shape: one 10,000-row match, ALL_ROWS, 50 SUM measures — O(n²) measure work (576 s in full).
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        SourceRows(Enumerable.Range(0, 10_000).Select(i => Row(("Id", $"{i:D5}"), ("Name", "a"), ("x", 1L))).ToArray());
        var measures = Enumerable.Range(0, 50).Select(i => ($"s{i}", "SUM(A.x)")).ToArray();
        var watch = Stopwatch.StartNew();

        var status = await StatusOfAsync(Req("A+", measures: measures, rows: ProtoRowsPerMatch.AllRowsShowEmpty, limit: 10_000),
            new PatternQueryLimitOptions { TimeoutSeconds = 1 });

        status.Should().Be(StatusCode.DeadlineExceeded);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the token is checked before each output row");
    }

    [Fact]
    public async Task After_limit_the_rpc_completes_without_awaiting_the_row_source_disposal()
    {
        // Option B of the §9.3 pause decision: a frozen StarRocks makes reader disposal wait until it resumes
        // (MatchRowsIntegrationTests proves the premise); the RPC must not wait with it.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var release = new TaskCompletionSource();
        var disposed = new TaskCompletionSource();
        _search.MatchRowsAsync(default!, default!, default!, default).ReturnsForAnyArgs(_ => Endless(release, disposed));

        var rpc = RunAsync(Req("A", partitionBy: ["Id"], limit: 2), new PatternQueryLimitOptions { BatchRows = 1 });

        (await rpc.WaitAsync(TimeSpan.FromSeconds(5))).Should().HaveCount(2);
        disposed.Task.IsCompleted.Should().BeFalse("disposal is still draining");
        release.SetResult();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        static async IAsyncEnumerable<IDictionary<string, object?>> Endless(TaskCompletionSource release, TaskCompletionSource disposed)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    await Task.Yield();
                    yield return Row(("Id", $"{i}"), ("Name", "a"));
                }
            }
            finally
            {
                await release.Task;
                disposed.SetResult();
            }
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~MatchPatternGrpcServiceTests"`
Expected: FAIL. Until Step 4, the generated base `MatchPattern` throws `RpcException(Unimplemented)`, so every test fails on status or output.

- [ ] **Step 4: Implement the RPC**

`Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.MatchPattern.cs`.

Use this code as written. The pieces that are easy to get subtly wrong:
- the order of spec §3.4 steps 1–7;
- the §6 mapping, with the timeout rule first;
- the one budget, which carries the token;
- the check before each row;
- option B's detached drain after `limit`.
```csharp
using System.Runtime.CompilerServices;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Patterns;
using Iverson.StarRocks;
using Iverson.Vector;
using Filter = Qdrant.Client.Grpc.Filter;
using EngineRowsPerMatch = Iverson.Patterns.RowsPerMatch;
using EngineSkipKind = Iverson.Patterns.AfterMatchSkipKind;
using ProtoRowsPerMatch = Iverson.Client.Contracts.RowsPerMatch;
using ProtoSkipKind = Iverson.Client.Contracts.AfterMatchSkipKind;

namespace Iverson.Api.Grpc;

public sealed partial class ObjectSearchGrpcService
{
    private readonly PatternQueryLimitOptions _patternLimits = patternLimits ?? PatternQueryLimitOptions.Default;
    private readonly IChunkRowSource _chunkRows = chunkRows ?? new QdrantChunkRowSource(vector, tenantScope);

    // ── MatchPattern (spec §3.4) ───────────────────────────────────────────────

    public override async Task MatchPattern(
        MatchPatternRequest request,
        IServerStreamWriter<MatchPatternResponse> responseStream,
        ServerCallContext context)
    {
        var limits = _patternLimits;
        var schema = RequireSchema(request.TypeName);                                   // step 1
        var chunks = request.Source == PatternRowSource.Chunks;

        var outputLimit = ValidateMatchPatternLimits(request, chunks, limits);          // step 2: §5, before Compile
        var compiled = CompileMatchPattern(request, limits);                            // step 2: Compile

        ChunkDescriptor? chunkDesc = null;                                               // step 3
        if (chunks)
        {
            chunkDesc = schema.ChunkFields.FirstOrDefault(c =>
                    string.Equals(c.PropertyName, request.ChunkProperty, StringComparison.OrdinalIgnoreCase))
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Property '{request.ChunkProperty}' on '{request.TypeName}' has no [IversonChunk] annotation."));
            if (schema.CollectionName is null)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Type '{request.TypeName}' has no Qdrant collection."));
        }

        // step 4: authorization; a denied caller gets an empty stream and no store is queried.
        IReadOnlyDictionary<string, AuthorizationConstraint> constraints;
        AuthorizationDecision? chunkDecision = null;
        string? tenantValue;
        IReadOnlySet<string>? allowedFields;
        if (chunks)
        {
            chunkDecision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
            if (chunkDecision.Denied) return;
            if (chunkDecision.AllowedFields is not null && !chunkDecision.AllowedFields.Contains(chunkDesc!.PropertyName))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Property '{request.ChunkProperty}' on '{request.TypeName}' is not authorized for this caller."));
            constraints = new Dictionary<string, AuthorizationConstraint>();
            tenantValue = chunkDecision.TenantValue;
            allowedFields = chunkDecision.AllowedFields;
        }
        else
        {
            var auth = EvaluateAuthorization(schema, []);
            if (auth.PrimaryDenied) return;
            constraints = auth.Constraints;
            tenantValue = auth.Constraints[schema.TypeName].TenantValue;
            allowedFields = auth.Constraints[schema.TypeName].AllowedFields;
        }

        var termVectorNames = ResolveSimilarityVectors(schema, compiled, chunks, chunkDesc, allowedFields);

        Filter? chunkFilter = null;
        if (chunks)
        {
            try
            {
                chunkFilter = BuildChunksFilter(schema, request.Where, chunkDecision!.AllowedFields);
            }
            catch (FilterTranslationException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
            chunkFilter = IntelligenceFilterBuilder.ApplyOwnership(
                chunkFilter, chunkDecision.OwnershipRequired,
                schema.Authorization?.OwnerField?.ToCamelCase(), chunkDecision.OwnerValue);
        }

        if (logger.IsEnabled(LogLevel.Information))                                     // step 5
            logger.LogInformation(
                "[MatchPattern] type={Type} source={Source} defines={Defines} measures={Measures}",
                request.TypeName.SanitizeForLog(), request.Source, request.Define.Count, request.Measures.Count);

        // steps 6–7. One timeout token per request (spec §5: TimeoutSeconds, or the client deadline if earlier —
        // context.CancellationToken already fires at the deadline), carried by the one per-request budget.
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
        var ct = timeout.Token;
        var budget = new PatternBudget(limits.MaxActiveThreads, limits.MaxSteps, ct);
        IAsyncEnumerator<PatternBatch>? batches = null;
        var detach = false;
        try
        {
            var termVectors = await EmbedSimilarityTermsAsync(schema, compiled, ct);
            var input = chunks
                ? ChunkInputAsync(new ChunkRowQuery(
                    tenantScope.ResolveCollectionName(schema.CollectionName!, tenantValue, isChunks: true),
                    chunkFilter, chunkDesc!.PropertyName,
                    compiled.SimilarityTerms.Count > 0 ? chunkDesc.PropertyName.ToSnakeCase() + "_vector" : null,
                    limits.BatchRows, limits.MaxRowsScanned), ct)
                : TypeRowsInputAsync(schema, request, compiled, constraints, limits, ct);

            batches = PatternPartitionBatcher.BatchAsync(
                input, chunks ? ["parent_key"] : request.PartitionBy.ToList(),
                limits.BatchRows, limits.MaxPartitionRows, chunks ? null : limits.MaxRowsScanned, ct)
                .GetAsyncEnumerator(ct);

            var written = 0;
            while (await batches.MoveNextAsync())
            {
                var batch = batches.Current;
                var scores = await ScoreBatchAsync(schema, batch, termVectorNames, termVectors, chunks, tenantValue, ct);
                var offset = 0;
                foreach (var partition in batch.Partitions)
                {
                    var baseRow = offset;
                    Func<int, int, double?> similarity = scores is null ? (_, _) => null : (row, term) => scores[baseRow + row][term];
                    foreach (var output in compiled.Run(partition.Select(r => r.Columns).ToList(), similarity, budget))
                    {
                        ct.ThrowIfCancellationRequested();                              // spec §3.4 step 6
                        var data = new Dictionary<string, object?>(output.Data, StringComparer.Ordinal);
                        AuthorizationFieldMasking.RemoveTenantColumn(data);
                        await responseStream.WriteAsync(
                            new MatchPatternResponse
                            {
                                Data = DictToProtoStruct(data), MatchNumber = output.MatchNumber,
                                Classifier = output.Classifier, TraceId = request.TraceId,
                            },
                            context.CancellationToken);
                        if (++written == outputLimit)
                        {
                            detach = true;                                              // step 7
                            return;
                        }
                    }
                    offset += partition.Count;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)                            // §6: checked before every other row
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "MatchPattern exceeded its time limit."));
        }
        catch (PatternEvaluationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (PatternBudgetExceededException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (EngagementQueryTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (FilterTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (EngagementNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"StarRocks is not ready: {ex.Message}"));
        }
        catch (EngagementStoreDisabledException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        finally
        {
            if (batches is not null && detach)
                _ = ReleaseDetachedAsync(batches, timeout);   // option B (§9.3 pause test): don't await the drain
            else
            {
                if (batches is not null) await batches.DisposeAsync();
                timeout.Dispose();
            }
        }
    }

    /// <summary>
    /// After <c>limit</c> the RPC completes without awaiting the row source's disposal: against a StarRocks that has
    /// stopped responding, disposing the reader drains until it answers again, and nothing bounds that (spec Known
    /// issues). The drain holds one pooled connection until then; the timeout source lives until it ends.
    /// </summary>
    private async Task ReleaseDetachedAsync(IAsyncEnumerator<PatternBatch> batches, CancellationTokenSource timeout)
    {
        try
        {
            await batches.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[MatchPattern] releasing the row source after the output limit failed.");
        }
        finally
        {
            timeout.Dispose();
        }
    }
```
The same file continues with the helpers below. Each rule is exact.

- **`ValidateMatchPatternLimits(request, chunks, limits)` → `int`** (spec §5, and the §1 source-shape rules). Each violation throws `RpcException(InvalidArgument)` whose message names the limit or rule. The checks:
  - `request.Pattern.Length > MaxPatternLength`
  - `request.Define.Count > MaxDefines`, `request.Measures.Count > MaxMeasures`, `request.Subsets.Count > MaxSubsets`
  - any `define` or `measures` `Expr.Length > MaxExpressionLength`
  - `request.Where.Count > queryLimits.MaxClauses`
  - `request.Limit < 0 || request.Limit > MaxOutputRows`
  - for CHUNKS:
    - `PartitionBy.Count > 0` or `OrderBy.Count > 0`
    - `Where.Count > 1 && WhereLogic == SearchLogic.Or` (spec §3.2: rejected, not ANDed)
  - for TYPE_ROWS: `OrderBy.Count == 0`

  Return `request.Limit == 0 ? PatternQueryLimitOptions.DefaultOutputRows : request.Limit`. These checks run before `Compile` because the engine's parsers recurse once per nesting level (plan Global Constraints).
- **`CompileMatchPattern(request, limits)` → `CompiledPattern`.** Build the engine request:
  ```csharp
  new PatternRequest(
      (PatternSource)(int)request.Source, request.PartitionBy.ToList(), request.Pattern,
      request.Subsets.Select(s => new SubsetDefinition(s.Name, s.Variables.ToList())).ToList(),
      request.Define.Select(d => new NamedExpression(d.Name, d.Expr)).ToList(),
      request.Measures.Select(m => new NamedExpression(m.Name, m.Expr)).ToList(),
      (EngineRowsPerMatch)(int)request.RowsPerMatch,
      (EngineSkipKind)(int)(request.AfterMatch?.Kind ?? ProtoSkipKind.PastLastRow),
      request.AfterMatch?.Variable ?? "",
      SchemaDescriptor.TenantColumnName);
  ```
  - The integer casts are exact, and `MatchPatternProtoTests` pins the alignment. `Compile` rejects undefined values.
  - Call `PatternQuery.Compile(…, limits.MaxProgramInstructions)`. Map `PatternValidationException` to `InvalidArgument`.
  - If `compiled.SimilarityTerms.Count > limits.MaxSimilarityTerms`, throw `InvalidArgument`.
- **`ResolveSimilarityVectors(schema, compiled, chunks, chunkDesc, allowedFields)` → `string[]`**, the vector name per term.
  - For CHUNKS, every term is `chunkDesc.PropertyName.ToSnakeCase() + "_vector"`. `Compile` already requires the column to be `text`.
  - For TYPE_ROWS, each term's column resolves case-insensitively to a `schema.VectorFields` descriptor. If none matches, throw `InvalidArgument` "… has no [IversonEmbedding] annotation".
  - When `allowedFields` is not null, the descriptor's `PropertyName` must be in it (`InvalidArgument` otherwise).
  - The name is `descriptor.PropertyName.ToSnakeCase() + "_vector"` (spec §2).
  - When any term exists and `schema.CollectionName is null`, throw `RpcException(FailedPrecondition, "Type '…' has no Qdrant collection.")` (§6).
- **`EmbedSimilarityTermsAsync(schema, compiled, ct)` → `float[][]`.**
  - With no terms, return `[]`.
  - Otherwise call `SimilarityResolver.EmbedAsync(resolver.Get(SchemaDescriptor.ModelOf(schema)), compiled.SimilarityTerms, ct)`.
  - Map `EmptyEmbeddingInputException` to `InvalidArgument`.
  - Map any other exception except `OperationCanceledException` to `logger.LogError(ex, "Embedding service unavailable")` plus `RpcException(Unavailable, "Embedding service unavailable.")`, exactly as `SearchChunks` does (`ObjectSearchGrpcService.cs:396-407`).
- **`TypeRowsInputAsync(schema, request, compiled, constraints, limits, [EnumeratorCancellation] ct)` → `IAsyncEnumerable<PatternInputRow>`.** Builds:
  ```csharp
  new MatchRowsRequest(request.Where.ToList(), request.WhereLogic, request.PartitionBy.ToList(),
      request.OrderBy.ToList(), compiled.ReferencedColumns.ToList(), compiled.MeasureNames,
      request.RowsPerMatch == ProtoRowsPerMatch.OneRow,
      schema.ScalarColumns.Where(c => string.Equals(c.SqlType, "BYTEA", StringComparison.OrdinalIgnoreCase))
          .Select(c => c.Name).ToList(),
      limits.MaxRowsScanned)
  ```
  It then yields `new PatternInputRow(row, null)` for each row of `search.MatchRowsAsync(SchemaBuilder.ToEngagementQuerySchema(schema), rowsRequest, constraints, ct)`.
- **`ChunkInputAsync(query, [EnumeratorCancellation] ct)` → `IAsyncEnumerable<PatternInputRow>`.** For each `ChunkRow c` of `_chunkRows.ReadAsync(query, ct)`, yields:
  ```csharp
  new PatternInputRow(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
      { ["parent_key"] = c.ParentKey, ["chunk_index"] = c.ChunkIndex, ["text"] = c.Text }, c.Vector)
  ```
  The key order is spec §1's column order, and `ALL_ROWS_*` emits the columns in the row's enumeration order.
- **`ScoreBatchAsync(schema, batch, termVectorNames, termVectors, chunks, tenantValue, ct)` → `double?[][]?`.**
  - With no terms, return `null`.
  - For CHUNKS, return `SimilarityResolver.ScoreVectors(batch rows' ChunkVector, termVectors)`.
  - For TYPE_ROWS, return `new SimilarityResolver(vector, tenantScope).ScoreRowsAsync(tenantScope.ResolveCollectionName(schema.CollectionName!, tenantValue, isChunks: false), batch rows' IntelligenceStoreConsumer.KeyToUlong((string)row.Columns[schema.KeyColumn.Name]!), termVectorNames, termVectors, ct)`.
  - Rows are indexed in batch order, so partition `p`'s row `r` is at `offset(p) + r`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Iverson.Server/Iverson.Api.Tests --filter "FullyQualifiedName~MatchPatternGrpcServiceTests|FullyQualifiedName~MatchPatternProtoTests|FullyQualifiedName~PatternPartitionBatcherTests"`
Expected: PASS. The container-backed classes are not selected, because their names don't match.

Run: `dotnet test Iverson.slnx --filter "Category!=Integration"`
Expected: PASS (the whole unit suite).

Run: `dotnet test Iverson.slnx --filter "Category=Integration&(FullyQualifiedName~MatchRows|FullyQualifiedName~ChunkRowSource|FullyQualifiedName~SimilarityResolver)"`
Expected: PASS. These are this plan's container tests: StarRocks, then Qdrant.

- [ ] **Step 6: Commit**
```bash
git add Iverson.Server/Iverson.Api/Grpc/PatternQueryLimitOptions.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.MatchPattern.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api/Grpc/AuthorizationFieldMasking.cs Iverson.Server/Iverson.Api.Tests/Grpc/MatchPatternGrpcServiceTests.cs
git commit -m "add the MatchPattern RPC over StarRocks rows and Qdrant chunks"
```

## Tasks NOT in this plan

From the spec's "Out of scope", verbatim:

- A Postgres row source; SQL pushdown of simple patterns; the window-clause form of row pattern
  recognition (`SEEK`/`INITIAL`).
- `SIMILARITY` on a related type's vector (through an FK), or on anything other than the row's own
  type (or, for `CHUNKS`, the chunk's own vector).
- Using a pattern result as a ranking signal inside `SearchSimilar`/`SearchChunks`.
- Changing `SearchChunks`' existing silent AND of an OR filter.

Plan 3 of 3 covers, from spec §3.5 and §9.5:
- **SDKs:** each of the five SDKs gets a `MatchPatternBuilder` and a `MatchPatternAsync` coordinator method, plus a regenerated proto for Python, TypeScript and Go.
- **Conformance:** the client-conformance cases (one scalar-pattern case and one `SIMILARITY` case).

Plan 1 (the engine) is on `main` at `156fb8c9`.

## Known issues inherited from spec

Inherited verbatim. The fourth bullet, "Open, decided by the §9.3 pause test…", has been decided: the user chose option B (Task 7's `ReleaseDetachedAsync`, with its premise pinned in Task 3's pause test). After `limit`, the RPC now completes, and the drain continues in the background, holding one pooled connection until StarRocks answers.

- Ingest lag between StarRocks and Qdrant produces `NULL` similarity (§8).
- History-dependent `define` predicates can make matching exponential in the worst case. It is
  bounded by `MaxActiveThreads`, `MaxSteps` and the timeout (through the budget's token), not prevented.
  Evaluating `measures` for output rows is bounded only by `limit` and the timeout, which is checked
  before each output row, so a timeout can overrun by one output row's `measures`.
- Compilation is bounded separately. `PERMUTE` expands to every ordering and bounded quantifiers
  unroll, so `MaxProgramInstructions` rejects with `InvalidArgument` some patterns well inside
  `MaxPatternLength`: at the default, `PERMUTE(A, B, C, D, E, F)` (5,759 instructions), `A{10000}`
  and `(A{0,100}){0,100}`.
- Open, decided by the §9.3 pause test: if StarRocks stops responding after a request has written
  `limit` rows, disposing the reader drains until StarRocks answers again, and no MySqlConnector
  call bounds it, so the request does not complete until then.
- The `CHUNKS` source scans the filtered chunk set twice (the phase-1 parent list, then the batched
  phase-2 reads) to keep memory bounded.
- `NaN` from a zero-magnitude vector is kept (§2): `NOT`/`<>` predicates over it with a non-`NULL`
  operand qualify the row, and how each SDK decodes a `NaN` `number_value` is unverified.
- The unconfigured-vector case (§3.3) is detected by Qdrant message text, which may change across
  Qdrant versions, and it would also mask a wrong-vector-name bug.
