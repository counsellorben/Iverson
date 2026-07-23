# Client Search-Family Execution Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-22-client-search-execution-parity-design.md` (commit SHA: `5d699e0a8927a443af3f65fdde204bb8f4ec8833`)

**Goal:** every Iverson client language (DotNet, Go, Java, Python, TypeScript) can actually execute all six search-family RPCs (Search, SearchSimilar, SearchChunks, GroupBy, Aggregate, Pipeline), not just build request objects for them.

**Architecture:** five independent per-language tasks, no cross-task dependencies. Each adds a new `AggregateBuilder` (no client-side builder for `AggregateRequest` exists anywhere today), the missing search-family execution methods, and acting-user support where it's missing — following that language's own established conventions exactly (mirroring each language's existing `GroupByBuilder`).

**Tech stack:** DotNet (net10.0, xUnit/NSubstitute/FluentAssertions), Go (module at `Iverson.Clients/Go`), Java (Maven, module `client`), Python (>=3.11, pytest), TypeScript (Node ESM, vitest) — all per the spec's verified assumptions.

---

## Global Constraints

- Every new `AggregateBuilder` mirrors that language's existing `GroupByBuilder` method names/shape exactly, using that language's own established instantiation pattern (public constructor for DotNet/Python/TypeScript classes; factory-function-only for Go; package-private constructor + `Query` factory method for Java).
- `AggregationSpec`'s `group_by_fields`/`expression` override fields are explicitly NOT covered by any language's new builder — left for a future follow-up.
- No retrofitting acting-user support onto already-working methods (DotNet's 4 existing search methods; any language's `persist`/`get`/`update`/`delete`).
- No changes to generated proto clients or `.proto` files.
- No MCP server work (`Iverson.Clients/McpServer/` doesn't exist yet — out of scope).

## File Structure

**DotNet:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Search/AggregateBuilder.cs` — fluent `AggregateRequest` builder
- Create: `Iverson.Clients/DotNet/Iverson.Client.Search.Tests/AggregateBuilderTests.cs`
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorGroupByTests.cs`
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorAggregateTests.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs` — add `GroupByAsync`/`AggregateAsync`

**Go:**
- Create: `Iverson.Clients/Go/iverson/aggregate.go` — fluent `AggregateRequest` builder
- Modify: `Iverson.Clients/Go/iverson/coordinator.go` — add `SearchClient` interface, `searchAdapter`, `structToMap`, 6 new `EntityCoordinator[T]` methods
- Create: `Iverson.Clients/Go/iverson_test/aggregate_test.go`
- Create: `Iverson.Clients/Go/iverson_test/coordinator_test.go` — first `EntityCoordinator`-level test file in Go

**Java:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/AggregateBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java` — add `aggregate(String)` factory
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java` — add 5 new methods + nested `ChunkSearchResult` record
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java` — add `fromStructAsMap`
- Create: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/AggregateBuilderTest.java`
- Create: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java` — first `EntityCoordinator`-level test file in Java

**Python:**
- Create: `Iverson.Clients/Python/iverson_client/aggregate.py`
- Modify: `Iverson.Clients/Python/iverson_client/core.py` — `IversonClient.__init__` acting-user param, `EntityCoordinator` 4th stub + 6 new methods
- Modify: `Iverson.Clients/Python/iverson_client/auth.py` — add `_ActingUserAuthPlugin`
- Create: `Iverson.Clients/Python/tests/test_aggregate.py`
- Create: `Iverson.Clients/Python/tests/test_entity_coordinator.py` — first `EntityCoordinator`-level test file in Python

**TypeScript:**
- Create: `Iverson.Clients/TypeScript/src/aggregate.ts`
- Modify: `Iverson.Clients/TypeScript/src/core.ts` — acting-user token param, `callUnary` metadata merge, 6 new `IversonClient` methods, `_buildRequest` extraction
- Modify: `Iverson.Clients/TypeScript/src/index.ts` — new exports
- Modify: `Iverson.Clients/TypeScript/package.json` — `main`/`types`/`exports`/`build`
- Create: `Iverson.Clients/TypeScript/tests/aggregate.test.ts`
- Create: `Iverson.Clients/TypeScript/tests/core.test.ts` — first `IversonClient`-execution-level test file in TypeScript

## Inherited from spec

The following were verified by `thorough-brainstorming` (and two rounds of `critical-design-review`) at spec-write time and are trusted as ground truth here (not re-verified):

1. Current per-language execution state (which of the 6 RPCs already work, and where acting-user support does/doesn't reach `EntityCoordinator`) — spec's "Current state (verified)" table.
2. No client-side request-builder for `AggregateRequest` exists in any of the 5 languages — `find . -iname "*aggregate*"` across all 5 trees, no hits outside an unrelated Java enum usage.
3. DotNet's generated `ObjectSearchServiceClient.AggregateAsync`/`GroupBy` signatures — `ObjectSearchGrpc.cs:229-259`.
4. `StructConverter.ToDictionary(Struct)` exists and is generic/reusable — `StructConverter.cs:50-51`.
5. Go's generated `ObjectSearchServiceClient` interface shape — `object_search_grpc.pb.go:35-42`. No existing Go test constructs `coordinatorDeps{}` or calls `newEntityCoordinatorWithDeps`.
6. `AbstractStub.withOption(CallOptions.Key<T>, T)` is a real grpc-java API. Java's `GroupByBuilder`/`SimilarBuilder`/`ChunksBuilder` are non-generic.
7. Python's generated stub shapes (`Search`/`SearchSimilar`/`SearchChunks`/`GroupBy`/`Pipeline` unary_stream, `Aggregate` unary_unary) — `object_search_pb2_grpc.py:38-64`. `grpc.composite_channel_credentials` accepts multiple call-credentials. Python's channel construction needed a branch-condition fix for `acting_user_token`-only (already applied to the spec).
8. No interface/ABC/protocol wraps `EntityCoordinator` in any of the 4 non-TypeScript languages.
9. TypeScript-specific findings inherited from the MCP server design's own verification: `package.json` consumability gap, `callUnary`'s hardcoded metadata, `ObjectSearchServiceClient`'s generated shape, `payloadToEntity`'s reusability.

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | None of the 5 languages' new files (`AggregateBuilder.*`, `aggregate_test.*`/`test_aggregate.*`/`aggregate.test.ts`, `EntityCoordinatorTest.*`/`coordinator_test.go`/`test_entity_coordinator.py`/`core.test.ts`) exist today | `find`/`grep` across all 5 language trees this session — no hits |
| 2 | Code validity | `GroupByBuilder.cs` (DotNet) read in full — `Where`/`Not`/`Having`/`Join`/metric methods and `Build()`'s duplicate-alias validation confirmed exactly as the plan describes | Full read of `Iverson.Client.Search/GroupByBuilder.cs` |
| 2b | Signature | `EntityCoordinator.cs`'s `PipelineAsync` (`:241-254`) and `SearchChunksAsync` (`:222-235`) confirmed at exactly those line ranges, with the exact shapes Task 1 Step 3 mirrors | Full read of `Iverson.Client.Core/EntityCoordinator.cs:185-286` |
| 3 | Signature | `AggregationSpec`'s C# generated properties: `Name`/`Type`/`Field`/`Size`/`CalendarInterval`/`TimeZone`/`RangeBuckets`/`GroupByFields`/`Expression`; `RangeBucket.From`/`.To` are `double?` (no wrapper-type handling needed) | `ObjectSearchGrpc.cs`-adjacent generated file, `ObjectSearch.cs:4757-4920` (AggregationSpec), `:5253-5330` (RangeBucket) |
| 4 | Consumer impact (Cat 6) | Nothing reflects over `EntityCoordinator<T>`'s method list (DI registers it via `AddTransient(typeof(EntityCoordinator<>))`, constructor injection only) | `grep -rn "GetMethods\|GetMethod("` across DotNet + server trees for "EntityCoordinator" — no hits |
| 5 | Command validity | `dotnet test <project> --filter FullyQualifiedName~X` is a valid xUnit invocation in this repo (xUnit + NSubstitute + FluentAssertions, no custom test-runner script) | `Iverson.Client.Core.Tests.csproj` read in full |
| 6 | Code validity | `coordinator.go` read in full — `PersistenceClient`/`persistenceAdapter`/`RetrievalStream` pattern, `structToEntity[T]`/`entityToStruct`/`protoValueToGoValue` helpers confirmed exactly as the plan describes | Full read of `Go/iverson/coordinator.go` (this session and prior) |
| 7 | Command validity | `go test ./iverson_test/... -run TestX` is valid from the Go client's module root | `Go/go.mod` exists at `Iverson.Clients/Go/` |
| 8 | Code validity | `GroupByBuilder.java` read in full — constructor is package-private, only reachable via `Query.groupBy(String)`; identical pattern confirmed for `QueryBuilder`, `PipelineBuilder`, `SimilarBuilder`, `ChunksBuilder` (all 5 existing Java builders) | Full read of `GroupByBuilder.java`, `Query.java`; `grep` for each builder's constructor visibility |
| 9 | Code validity | `StructConverter.java` has exactly `toStruct(Object)`/`fromStruct(Struct, Class<T>)` as its only public methods (123 lines total) — no untyped conversion exists to reuse | Full read of `StructConverter.java` |
| 10 | File path | `SearchResult<T>` is nested inside `EntityCoordinator.java` at line 170, not a separate file — `ChunkSearchResult` follows the same placement | `EntityCoordinator.java:26,170` |
| 11 | Command validity | `mvn -pl client test -Dtest=X` is valid — `client` is the correct Maven module name (parent `pom.xml` declares `<module>client</module>`) | `Iverson.Clients/Java/pom.xml:17`, `client/pom.xml:14` |
| 12 | Consumer impact (Cat 6) | Nothing reflects over `Query`/`StructConverter`'s methods | `grep -rn "getMethods\|getDeclaredMethods"` across Java main source for "Query"/"StructConverter" — no hits |
| 13 | Code/line | `IversonClient.__init__`'s credentials branch is at `core.py:370` (`if credentials is not None:`), the `composite_channel_credentials` call at `core.py:378` | `grep -n` this session |
| 14 | Command validity | `python -m pytest tests/test_X.py` is valid — `pytest>=8.0` is a real dev dependency, no custom pytest config overriding invocation | `pyproject.toml:1-19` |
| 15 | Consumer impact (Cat 6) | Only one existing caller of Python `IversonClient(...)` (`tests/test_auth.py:17-21`), using `host=`/`port=`/`credentials=` keyword args exclusively — a new keyword-only `acting_user_token` param with a default is non-breaking | `grep -rn "IversonClient("` across Python tree; read of the one call site |
| 16 | Consumer impact (Cat 6) | Only one existing caller of TypeScript `new IversonClient(...)` (`sample/main.ts:12`), 2 positional args (`'localhost', 5000`) — a new 5th optional positional param is non-breaking | `grep -rn "new IversonClient("` across TypeScript tree this session |
| 17 | Command validity | `npx tsc --noEmit` compiles cleanly today (no pre-existing errors) — `npm run build`/`npm test` (`vitest run`) are valid once `package.json`'s scripts are added | Ran `npx tsc --noEmit` this session (clean); `package.json:6` |
| 18 | Consumer impact (Cat 6) | `tests/schema-registrar.test.ts` calls `_buildRequest(...)` directly **12 times** (not 5 as first estimated) — `_buildRequest`'s existing signature must be preserved when its internals delegate to the new extracted function | `grep -c "_buildRequest("` this session |
| 19 | Task ordering | All 5 tasks are independent — separate language ecosystems with no cross-language import mechanism in this repo | Structural (5 distinct package/module systems); no shared code between them |

## Tasks

### Task 1: DotNet — `AggregateBuilder` + `GroupByAsync`/`AggregateAsync`

**Files:**
- Create: `Iverson.Clients/DotNet/Iverson.Client.Search/AggregateBuilder.cs`
- Create: `Iverson.Clients/DotNet/Iverson.Client.Search.Tests/AggregateBuilderTests.cs`
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorGroupByTests.cs`
- Create: `Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorAggregateTests.cs`
- Modify: `Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs`

- [ ] **Step 1: Write `AggregateBuilderTests.cs`, then `AggregateBuilder.cs`**

`AggregateBuilder` mirrors `GroupByBuilder.cs` exactly: `Where`/`Not`/`AddWhere` builds `SearchClause` via `SearchValueConverter.ToSearchValue`; `Having`/`WithHavingLogic` the same way; `Join` builds `JoinSpec{LeftType=_typeName,...}`; `Build()` validates duplicate aliases via `HashSet<string>(StringComparer.OrdinalIgnoreCase)`. `Terms`/`DateHistogram`/`Range`/`Avg`/`Sum`/`Min`/`Max`/`Count`/`CountAll` each construct one `AggregationSpec` (properties: `Name`/`Type`/`Field`/`Size`/`CalendarInterval`/`TimeZone`/`RangeBuckets`; `RangeBucket.Key`/`.From`/`.To`, `From`/`To` are plain `double?`) via an `AddMetric`-style private helper, appended to `AggregateRequest.Aggregations`; validate duplicate `name` the same way `GroupByBuilder.Build()` validates duplicate metric aliases. Do not set `GroupByFields`/`Expression` (out of scope per Global Constraints).

- [ ] **Step 2: Build and run new builder tests**
```bash
dotnet test Iverson.Clients/DotNet/Iverson.Client.Search.Tests/Iverson.Client.Search.Tests.csproj --filter FullyQualifiedName~AggregateBuilderTests
```

- [ ] **Step 3: Write `EntityCoordinatorGroupByTests.cs`/`EntityCoordinatorAggregateTests.cs`, then add `GroupByAsync`/`AggregateAsync` to `EntityCoordinator.cs`**

Mirror `PipelineAsync`'s exact shape (`EntityCoordinator.cs:241-254`) for `GroupByAsync` (stream `search.GroupBy(request, headers, cancellationToken: ct)`, `yield return StructConverter.ToDictionary(response.Data)`) and `SearchChunksAsync`'s shape (`:222-235`) for `AggregateAsync` (unary `await search.AggregateAsync(request, headers, cancellationToken: ct)`, return as-is). `search` (`ObjectSearchService.ObjectSearchServiceClient`) is already an injected constructor parameter (`EntityCoordinator.cs:21`) and already registered in DI (`ServiceCollectionExtensions.cs:48`) — no DI changes needed.

- [ ] **Step 4: Build and run**
```bash
dotnet test Iverson.Clients/DotNet/Iverson.Client.Core.Tests/Iverson.Client.Core.Tests.csproj --filter "FullyQualifiedName~EntityCoordinatorGroupByTests|FullyQualifiedName~EntityCoordinatorAggregateTests"
```

- [ ] **Step 5: Commit**
```bash
git add Iverson.Clients/DotNet/Iverson.Client.Search/AggregateBuilder.cs Iverson.Clients/DotNet/Iverson.Client.Search.Tests/AggregateBuilderTests.cs Iverson.Clients/DotNet/Iverson.Client.Core/EntityCoordinator.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorGroupByTests.cs Iverson.Clients/DotNet/Iverson.Client.Core.Tests/EntityCoordinatorAggregateTests.cs
git commit -m "feat(dotnet-client): add AggregateBuilder and GroupByAsync/AggregateAsync execution"
```

---

### Task 2: Go — `AggregateBuilder` + 6 execution methods

**Files:**
- Create: `Iverson.Clients/Go/iverson/aggregate.go`
- Modify: `Iverson.Clients/Go/iverson/coordinator.go`
- Create: `Iverson.Clients/Go/iverson_test/aggregate_test.go`
- Create: `Iverson.Clients/Go/iverson_test/coordinator_test.go`

- [ ] **Step 1: Write `aggregate_test.go`, then `aggregate.go`**

Mirror `group_by.go`'s `GroupByBuilder` structure exactly (same field-list-plus-`Build()`-validation shape). Same `AggregationSpec`/`RangeBucket` field mapping as Task 1's DotNet `AggregateBuilder` — proto field names are identical across languages, only casing/idiom differs.

- [ ] **Step 2: Run new builder tests**
```bash
cd Iverson.Clients/Go && go test ./iverson_test/ -run TestAggregate
```

- [ ] **Step 3: Add `SearchClient` interface + `searchAdapter` + `SearchStream` interface to `coordinator.go`**

Mirrors `PersistenceClient`/`persistenceAdapter` and `RetrievalStream` exactly. Add a `search` field to `coordinatorDeps`, wire it in `NewEntityCoordinator` and `newEntityCoordinatorWithDeps`.

- [ ] **Step 4: Add `structToMap(*structpb.Struct) map[string]any`**

New unexported function alongside the existing `structToEntity[T]`/`entityToStruct` in `coordinator.go` — a direct field-by-`Value`-kind loop (mirrors `protoValueToGoValue`'s kind-switch, but returns `any` per field instead of writing into a target `reflect.Type`).

- [ ] **Step 5: Write `coordinator_test.go`, then add the 6 new `EntityCoordinator[T]` methods**

`Search`/`SearchSimilar` use `structToEntity[T]`; `GroupBy`/`Pipeline` use the new `structToMap`; `SearchChunks` returns `*pb.ChunkSearchResponse` unconverted; `Aggregate` is a single call returning `*pb.AggregateResponse` unconverted. All six take `ctx context.Context` first, matching every existing method — no `auth.go` changes (the existing `WithActingUserToken(ctx, token)` already composes automatically via `OAuth2ClientCredentials.GetRequestMetadata`).

- [ ] **Step 6: Run all new tests**
```bash
cd Iverson.Clients/Go && go test ./iverson_test/... -run "TestAggregate|TestCoordinator"
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Clients/Go/iverson/aggregate.go Iverson.Clients/Go/iverson/coordinator.go Iverson.Clients/Go/iverson_test/aggregate_test.go Iverson.Clients/Go/iverson_test/coordinator_test.go
git commit -m "feat(go-client): add AggregateBuilder and 6 search-family execution methods"
```

---

### Task 3: Java — `AggregateBuilder` + 5 execution methods

**Files:**
- Create: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/AggregateBuilder.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java`
- Modify: `Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java`
- Create: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/AggregateBuilderTest.java`
- Create: `Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java`

- [ ] **Step 1: Write `AggregateBuilderTest.java`, then `AggregateBuilder.java` + `Query.aggregate(String)`**

Mirror `GroupByBuilder.java` exactly: package-private constructor (`AggregateBuilder(String typeName)`), instantiated only via a new `public static AggregateBuilder aggregate(String typeName)` on `Query.java` (matching `Query.groupBy`/`Query.pipeline`/`Query.similar`/`Query.chunks`'s identical pattern — confirmed this is the universal pattern for all 5 existing Java builders, not a public constructor). `where`/`not`/`withLogic`/`having`/`withHavingLogic`/`join` mirror `GroupByBuilder`'s identical methods verbatim (same `SearchClause.newBuilder()`/`JoinSpec.newBuilder()` calls). `terms`/`dateHistogram`/`range`/`avg`/`sum`/`min`/`max`/`count`/`countAll` each build one `AggregationSpec` via an `addMetric`-style private helper (mirroring `GroupByBuilder.addMetric`), appended to `AggregateRequest.Aggregations`. `build(String traceId)` validates duplicate `name` the same way `GroupByBuilder.build()` validates duplicate metric aliases (`HashSet<String>` + lowercase dedup).

- [ ] **Step 2: Compile and run new builder tests**
```bash
cd Iverson.Clients/Java && mvn -pl client test -Dtest=AggregateBuilderTest
```

- [ ] **Step 3: Add `StructConverter.fromStructAsMap(Struct)`**

New public static method in `StructConverter.java` (confirmed: currently only `toStruct(Object)`, `fromStruct(Struct, Class<T>)`, and private helpers exist), mirroring `fromValue`'s per-`Value`-kind unwrapping but returning a `Map<String, Object>` directly instead of populating a target class's fields.

- [ ] **Step 4: Write `EntityCoordinatorTest.java`, then add the 5 new `EntityCoordinator<T>` methods + nested `ChunkSearchResult` record**

New file — Java has no `EntityCoordinator`-level test today; establish it following `SchemaRegistrarTest.java`'s mocking convention. Add `groupBy`/`aggregate`/`pipeline`/`searchSimilar`/`searchChunks` (each with a `null`-token overload), signatures: `groupBy(GroupByBuilder builder, String actingUserToken)`, `aggregate(AggregateBuilder builder, String actingUserToken)`, `pipeline(PipelineBuilder builder, String actingUserToken)`, `searchSimilar(SimilarBuilder builder, String actingUserToken)`, `searchChunks(ChunksBuilder builder, String actingUserToken)` — none of the builder params take a `<T>` (confirmed non-generic). Add `public record ChunkSearchResult(String parentKey, String chunkText, float score) {}` nested in `EntityCoordinator.java`, matching exactly where `SearchResult<T>` already lives (`EntityCoordinator.java:170`). When `actingUserToken != null`, call via `client.searchStub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, actingUserToken)`.

- [ ] **Step 5: Compile and run**
```bash
cd Iverson.Clients/Java && mvn -pl client test -Dtest=EntityCoordinatorTest
```

- [ ] **Step 6: Commit**
```bash
git add Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/AggregateBuilder.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/search/Query.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java Iverson.Clients/Java/client/src/main/java/io/iverson/client/core/StructConverter.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/search/AggregateBuilderTest.java Iverson.Clients/Java/client/src/test/java/io/iverson/client/core/EntityCoordinatorTest.java
git commit -m "feat(java-client): add AggregateBuilder and 5 search-family execution methods"
```

---

### Task 4: Python — `AggregateBuilder` + 6 execution methods + acting-user token

**Files:**
- Create: `Iverson.Clients/Python/iverson_client/aggregate.py`
- Modify: `Iverson.Clients/Python/iverson_client/core.py`
- Modify: `Iverson.Clients/Python/iverson_client/auth.py`
- Create: `Iverson.Clients/Python/tests/test_aggregate.py`
- Create: `Iverson.Clients/Python/tests/test_entity_coordinator.py`

- [ ] **Step 1: Write `test_aggregate.py`, then `aggregate.py`**

Mirror `group_by.py`'s `GroupByBuilder`/`group_by()` pair exactly (public `__init__`, plus a module-level `def aggregate(type_name: str) -> AggregateBuilder:` convenience function, matching `group_by()`'s identical pattern). `where`/`not_`/`with_logic`/`having`/`with_having_logic`/`join` mirror `GroupByBuilder`'s identical methods. `terms`/`date_histogram`/`range`/`avg`/`sum`/`min`/`max`/`count`/`count_all` each build one `AggregationSpec`, mirroring `GroupByBuilder._add_metric`. `build(trace_id="")` validates duplicate `name` the same way.

- [ ] **Step 2: Run new builder tests**
```bash
cd Iverson.Clients/Python && python -m pytest tests/test_aggregate.py
```

- [ ] **Step 3: Add `_ActingUserAuthPlugin` to `auth.py`**

Mirrors `_BearerTokenAuthPlugin` exactly, except its `__call__` emits `x-acting-user-authorization` (not `authorization`) and wraps a static token string directly (not a `_CachedTokenProvider` — the acting-user token is pre-minted, no refresh logic needed).

- [ ] **Step 4: Modify `IversonClient.__init__`**

Add `acting_user_token: str | None = None` keyword param. When set, compose a second `grpc.metadata_call_credentials(_ActingUserAuthPlugin(acting_user_token))` into the same `grpc.composite_channel_credentials(...)` call (`core.py:378`) alongside the existing `call_creds`. Change the branch condition at `core.py:370` from `if credentials is not None:` to `if credentials is not None or acting_user_token is not None:`.

- [ ] **Step 5: Write `test_entity_coordinator.py`, then add the fourth stub + 6 new `EntityCoordinator` methods**

New file, named after the class per the `test_schema_registrar.py` precedent. Add `self._search = search_grpc.ObjectSearchServiceStub(channel)` to `EntityCoordinator.__init__`. Add `search`/`search_similar`/`group_by`/`aggregate`/`search_chunks`/`pipeline`: `search`/`search_similar` convert via the existing `_from_struct`; `group_by`/`pipeline` convert via the existing (currently unused) `_struct_to_dict` (`core.py:217-218`); `search_chunks` returns flat messages as-is; `aggregate` is a single call returning `AggregateResponse` as-is.

- [ ] **Step 6: Run all new/changed tests**
```bash
cd Iverson.Clients/Python && python -m pytest tests/test_aggregate.py tests/test_entity_coordinator.py tests/test_auth.py
```

- [ ] **Step 7: Commit**
```bash
git add Iverson.Clients/Python/iverson_client/aggregate.py Iverson.Clients/Python/iverson_client/core.py Iverson.Clients/Python/iverson_client/auth.py Iverson.Clients/Python/tests/test_aggregate.py Iverson.Clients/Python/tests/test_entity_coordinator.py
git commit -m "feat(python-client): add AggregateBuilder, 6 search-family execution methods, and acting-user token support"
```

---

### Task 5: TypeScript — `AggregateBuilder` + package setup + 6 execution methods + acting-user token

**Files:**
- Create: `Iverson.Clients/TypeScript/src/aggregate.ts`
- Modify: `Iverson.Clients/TypeScript/src/core.ts`
- Modify: `Iverson.Clients/TypeScript/src/index.ts`
- Modify: `Iverson.Clients/TypeScript/package.json`
- Create: `Iverson.Clients/TypeScript/tests/aggregate.test.ts`
- Create: `Iverson.Clients/TypeScript/tests/core.test.ts`

- [ ] **Step 1: Add `main`/`types`/`exports`/`build` to `package.json`**
```json
"main": "dist/index.js",
"types": "dist/index.d.ts",
"exports": { ".": { "types": "./dist/index.d.ts", "default": "./dist/index.js" } },
"scripts": { "build": "tsc", "test": "vitest run", "generate": "bash scripts/generate_protos.sh" }
```

- [ ] **Step 2: Write `tests/aggregate.test.ts`, then `src/aggregate.ts`**

Mirror `group-by.ts`'s `GroupByBuilder`/`groupBy()` pair exactly (public constructor, `this`-returning chain, plus a module-level `export function aggregate(typeName: string): AggregateBuilder`). `where`/`not`/`withLogic`/`having`/`withHavingLogic`/`join`/`terms`/`dateHistogram`/`range`/`avg`/`sum`/`min`/`max`/`count`/`countAll`/`build` mirror `GroupByBuilder`'s identical methods (same duplicate-alias validation in `build()`). Export `AggregateBuilder, aggregate` from `index.ts` alongside `GroupByBuilder, groupBy`.

- [ ] **Step 3: Export `createOAuth2ClientCredentials`/`createActingUserMetadata` from `index.ts`**

Both already implemented in `auth.ts` but missing from `index.ts`'s export list — add them.

- [ ] **Step 4: Add acting-user token support to `IversonClient`**

`IversonClient`'s constructor gains a 5th optional param `actingUserToken?: string | (() => Promise<string>)`, stored as `readonly _actingUserToken`. `callUnary` (currently hardcodes `new grpc.Metadata()` as its 2nd arg to every call) gains a new optional parameter for this token; when present, resolves it (awaiting if it's a function) and merges via the existing `createActingUserMetadata` into the metadata object passed to the gRPC call. Thread `this._client._actingUserToken` through every existing `callUnary` call site in `EntityCoordinator` (`persist`/`update`/`delete`/`get`) and the streaming `getMany` call (which also currently hardcodes `new grpc.Metadata()`).

- [ ] **Step 5: Write `tests/core.test.ts`, then add the 6 new `IversonClient` methods + `ObjectSearchServiceClient` field**

New file — no existing execution-level test for `IversonClient`'s methods today. `search`/`groupBy`/`pipeline`/`searchSimilar` share one Struct-conversion path reusing the existing `payloadToEntity`, called with an entity class for `search`/`searchSimilar` and without one (plain `Record<string, unknown>`) for `groupBy`/`pipeline`. `aggregate`/`searchChunks` are typed pass-throughs. Same `actingUserToken` threading as Step 4.

- [ ] **Step 6: Promote `SchemaRegistrar._buildRequest`'s field-reflection logic into a shared exported function**

Extract the property/relation-reflection loop (`_buildRequest`) into a new exported function (e.g. `describeEntity(cls: Function): TypeDescriptor`-shaped object). `_buildRequest` itself keeps its existing signature and calls the new function internally — `tests/schema-registrar.test.ts` calls `registrar._buildRequest(...)` directly in **12 places** and must keep passing unchanged.

- [ ] **Step 7: Build and run all tests**
```bash
cd Iverson.Clients/TypeScript && npm run build && npm test
```

- [ ] **Step 8: Commit**
```bash
git add Iverson.Clients/TypeScript/src/aggregate.ts Iverson.Clients/TypeScript/src/core.ts Iverson.Clients/TypeScript/src/index.ts Iverson.Clients/TypeScript/package.json Iverson.Clients/TypeScript/tests/aggregate.test.ts Iverson.Clients/TypeScript/tests/core.test.ts
git commit -m "feat(ts-client): add AggregateBuilder, 6 search-family execution methods, acting-user token support, and package.json consumability"
```

## Tasks NOT in this plan

- Retrofitting acting-user support onto already-working methods (DotNet's 4 existing search methods; any language's `persist`/`get`/`update`/`delete`).
- Any change to the generated proto clients or `.proto` files themselves.
- The MCP server itself (`Iverson.Clients/McpServer/`) — this design closes its prerequisite; that work resumes per `docs/specs/2026-07-22-mcp-server-design.md` Phase B once this lands.
