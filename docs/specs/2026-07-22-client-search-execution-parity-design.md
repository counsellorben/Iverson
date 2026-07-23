# Client Search-Family Execution Parity — Design

**Goal:** every Iverson client language (DotNet, Go, Java, Python, TypeScript) can actually *execute* all six search-family RPCs (Search, SearchSimilar, SearchChunks, GroupBy, Aggregate, Pipeline), not just build request objects for them.

**Origin:** discovered while designing the Iverson MCP server (`docs/specs/2026-07-22-mcp-server-design.md`). That design's Phase A found TypeScript builds requests for all six but executes none of them. Checking the other four clients surfaced the same family of gap at different completion levels — this design closes it everywhere before the MCP server work resumes.

**Relationship to the MCP server design:** this design **supersedes Phase A** of `docs/specs/2026-07-22-mcp-server-design.md` for TypeScript's slice (the content is unchanged — reproduced here as this design's TypeScript section for completeness). That document's Phase B (the MCP server itself) is unaffected and resumes once this lands.

---

## Current state (verified)

| Client | Search | SearchSimilar | SearchChunks | Pipeline | GroupBy | Aggregate | Acting-user support reaching `EntityCoordinator` |
|---|---|---|---|---|---|---|---|
| **DotNet** | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | Only `PersistAsync` (`Metadata? headers` param) |
| **Go** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Free on every method, via `context.Context` |
| **Java** | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | None — mechanism exists (`CallOptions.Key`) but nothing sets it |
| **Python** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | None |
| **TypeScript** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | None (`callUnary` hardcodes empty metadata) |

Where a ✅ already exists, that method is untouched by this design — only the gaps are closed.

**Aggregate is a special case:** unlike the other five operations, no client-side request-builder for `AggregateRequest` exists in *any* of the 5 languages today (confirmed via CDR round 1 — every language has a `GroupByBuilder`/`PipelineBuilder`/equivalent, but none has an `AggregateBuilder`/equivalent). Closing Aggregate's execution gap therefore also requires designing and adding a new builder, in every language, alongside the new execution method — each language's section below covers both.

---

## DotNet

Two new methods on `EntityCoordinator<T>` (`Iverson.Client.Core/EntityCoordinator.cs`), following the exact structure of the four already-working search methods and `PersistAsync`'s header pattern:

```
GroupByAsync(GroupByBuilder groupBy, Metadata? headers = null, CancellationToken ct = default)
    → IAsyncEnumerable<IReadOnlyDictionary<string, object?>>
```
Streams `search.GroupBy(request, headers, cancellationToken: ct)`, converting each row via `StructConverter.ToDictionary` — the same helper `PipelineAsync` already uses (dynamic column set, same reasoning as Pipeline).

```
AggregateAsync(AggregateBuilder aggregate, Metadata? headers = null, CancellationToken ct = default)
    → Task<AggregateResponse>
```
Unary call to `search.AggregateAsync(request, headers, cancellationToken: ct)`, returned as-is — `AggregateResponse` is already typed, no Struct conversion applies (same reasoning as `SearchChunksAsync`).

**New: `AggregateBuilder`** (`Iverson.Client.Search/AggregateBuilder.cs`) — no `AggregateRequest` builder exists in DotNet today, so `AggregateAsync` above has nothing to take as its argument until this is added. Fluent, `sealed class AggregateBuilder`, mirroring `GroupByBuilder.cs`'s existing method names/shape exactly:

```csharp
public AggregateBuilder(string typeName)
public AggregateBuilder Where(string field, SearchOperator op, object value)
public AggregateBuilder Not(string field, SearchOperator op, object value)
public AggregateBuilder WithLogic(SearchLogic logic)
public AggregateBuilder Having(string alias, SearchOperator op, object value)
public AggregateBuilder WithHavingLogic(SearchLogic logic)
public AggregateBuilder Join(string leftField, string rightType, string rightField, JoinKind kind = JoinKind.Inner)
public AggregateBuilder Terms(string field, string name, int size = 10)
public AggregateBuilder DateHistogram(string field, string name, string calendarInterval, string timeZone = "")
public AggregateBuilder Range(string field, string name, IEnumerable<(string? key, double? from, double? to)> buckets)
public AggregateBuilder Avg(string field, string name)
public AggregateBuilder Sum(string field, string name)
public AggregateBuilder Min(string field, string name)
public AggregateBuilder Max(string field, string name)
public AggregateBuilder Count(string field, string name)
public AggregateBuilder CountAll(string name)
public AggregateRequest Build(string traceId = "")
```

`Where`/`Not`/`WithLogic` build `AggregateRequest.Query`; `Having`/`WithHavingLogic` build `AggregateRequest.Having`; `Join` builds `AggregateRequest.Joins` — all three mirror `GroupByBuilder`'s identical methods exactly. `Terms`/`DateHistogram`/`Range`/`Avg`/`Sum`/`Min`/`Max`/`Count`/`CountAll` each append one `AggregationSpec` (covering `AggregationType`'s `TERMS`/`DATE_HISTOGRAM`/`RANGE`/`AVG`/`SUM`/`MIN`/`MAX`/`COUNT` variants respectively) to `AggregateRequest.Aggregations`, keyed by `name` (the spec's required-unique result key). `AggregationSpec`'s `group_by_fields`/`expression` override fields (multi-key `TERMS` and raw-SQL-expression aggregations) are not covered by this minimal builder — left for a follow-up if needed, same as how `GroupByBuilder`'s `SumExpr`/`AvgExpr` were added after its simple metrics.

`headers` threads through exactly like `PersistAsync` already does. New tests: `EntityCoordinatorGroupByTests.cs` / `EntityCoordinatorAggregateTests.cs`, matching the existing `EntityCoordinatorPipelineTests.cs` pattern, plus an `AggregateBuilderTests.cs` in `Iverson.Client.Search.Tests` matching `GroupByBuilder`'s own test file.

---

## Go

Six new methods on `EntityCoordinator[T]` (`Go/iverson/coordinator.go`), following the existing adapter-interface pattern used for `PersistenceClient`/`RetrievalClient`/`MappingDeleteClient`:

- New `SearchClient` interface (`Search`, `SearchSimilar`, `SearchChunks`, `GroupBy`, `Aggregate`, `Pipeline`) + a `searchAdapter` wrapping `pb.ObjectSearchServiceClient`, added to `coordinatorDeps` and wired in `NewEntityCoordinator` — mirrors `persistenceAdapter`/`retrievalAdapter`/`mappingDeleteAdapter`.
- `Search`, `SearchSimilar` stream `*pb.SearchResponse` (a new `SearchStream` interface plays the same role as the existing `RetrievalStream`), converting each row with the existing `structToEntity[T]` helper — both genuinely return `T`-shaped entities.
- `GroupBy`, `Pipeline` also stream `*pb.SearchResponse`, but their columns are aggregated/aliased and don't match `T`'s own fields — converting them with `structToEntity[T]` would silently zero out most fields. They instead use a new untyped `structToMap(*structpb.Struct) map[string]any` helper (alongside the existing `entityToStruct`/`structToEntity[T]` in `coordinator.go`), matching DotNet's `StructConverter.ToDictionary`, whose own doc comment names exactly this case ("a Pipeline/GroupBy result... without forcing it through a typed POCO").
- `SearchChunks` streams `*pb.ChunkSearchResponse` — same loop shape, no conversion.
- `Aggregate` is a single call, returns `*pb.AggregateResponse` as-is (Go's generated client already returns this directly, not via callback).

All six take `ctx context.Context` as their first parameter, matching every existing method. A caller attaches the acting-user token via the already-existing `iverson.WithActingUserToken(ctx, token)` before calling — no changes needed to `auth.go` or the credentials layer.

**New: `AggregateBuilder`** (`Go/iverson/aggregate.go`) — no `AggregateRequest` builder exists in Go today; `Aggregate` above has nothing to call `.Build()` on until this is added. Mirrors `group_by.go`'s `GroupByBuilder` method names/shape exactly:

```go
func NewAggregate(typeName string) *AggregateBuilder
func (a *AggregateBuilder) Where(field string, op pb.SearchOperator, val *pb.SearchValue) *AggregateBuilder
func (a *AggregateBuilder) Not(field string, op pb.SearchOperator, val *pb.SearchValue) *AggregateBuilder
func (a *AggregateBuilder) WithLogic(logic pb.SearchLogic) *AggregateBuilder
func (a *AggregateBuilder) Having(alias string, op pb.SearchOperator, val *pb.SearchValue) *AggregateBuilder
func (a *AggregateBuilder) WithHavingLogic(logic pb.SearchLogic) *AggregateBuilder
func (a *AggregateBuilder) Join(leftField, rightType, rightField string, opts ...pb.JoinKind) *AggregateBuilder
func (a *AggregateBuilder) Terms(field, name string, size ...int32) *AggregateBuilder
func (a *AggregateBuilder) DateHistogram(field, name, calendarInterval string, timeZone ...string) *AggregateBuilder
func (a *AggregateBuilder) Range(field, name string, buckets ...RangeBucket) *AggregateBuilder
func (a *AggregateBuilder) Avg(field, name string) *AggregateBuilder
func (a *AggregateBuilder) Sum(field, name string) *AggregateBuilder
func (a *AggregateBuilder) Min(field, name string) *AggregateBuilder
func (a *AggregateBuilder) Max(field, name string) *AggregateBuilder
func (a *AggregateBuilder) Count(field, name string) *AggregateBuilder
func (a *AggregateBuilder) CountAll(name string) *AggregateBuilder
func (a *AggregateBuilder) Build(traceId ...string) (*pb.AggregateRequest, error)
```

Same field mapping and same `group_by_fields`/`expression`-override omission as DotNet's `AggregateBuilder` above (see that section for the full rationale — not repeated per language).

New tests follow the mock-injection scaffolding already present (`newEntityCoordinatorWithDeps`) — currently unused by any test, since Go has no existing `EntityCoordinator`-level test file at all (only builder tests exist today) — plus a new `aggregate_test.go` matching `group_by_test.go`'s existing shape.

---

## Java

Five new methods on `EntityCoordinator<T>` (`Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java`):

```java
public List<Map<String, Object>> groupBy(GroupByBuilder builder, String actingUserToken)
public AggregateResponse aggregate(AggregateBuilder builder, String actingUserToken)
public List<Map<String, Object>> pipeline(PipelineBuilder builder, String actingUserToken)
public List<SearchResult<T>> searchSimilar(SimilarBuilder builder, String actingUserToken)
public List<ChunkSearchResult> searchChunks(ChunksBuilder builder, String actingUserToken)
```

`GroupByBuilder`/`SimilarBuilder`/`ChunksBuilder` are non-generic (`public final class GroupByBuilder`, no `<T>` — confirmed via `GroupByBuilder.java:42`, `SimilarBuilder.java:17`, `ChunksBuilder.java:12`), so none of these signatures take a type parameter — only `QueryBuilder<T>` (used by the existing `search()`) is generic.

Each also gets a no-token overload delegating with `null`. `groupBy`/`pipeline` return untyped `List<Map<String, Object>>` (dynamic column set) — `StructConverter.java` currently has only `toStruct(Object)` and `fromStruct(Struct, Class<T>)`, neither of which produces an untyped map, so this also requires adding `StructConverter.fromStructAsMap(Struct)` (mirroring DotNet's `StructConverter.ToDictionary`) as a small new helper. `aggregate` returns the typed `AggregateResponse` as-is. `searchSimilar` reuses the existing `SearchResult<T>` record. `searchChunks` returns a new small `ChunkSearchResult` record (`parent_key`/`chunk_text`/`score`) — Java has no existing equivalent to reuse, unlike DotNet's generated `ChunkSearchResponse`.

When `actingUserToken` is non-null, the call goes through `client.searchStub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, actingUserToken)` — the mechanism `applyRequestMetadata` already reads (`OAuth2ClientCredentials.java:56-59`), exercised from `EntityCoordinator` for the first time. `search()` itself is unchanged.

**New: `AggregateBuilder`** (`Java/client/src/main/java/io/iverson/client/search/AggregateBuilder.java`) — no `AggregateRequest` builder exists in Java today (confirmed: `find . -iname "*aggregate*"` under `client/src/main/java` returns nothing); `aggregate()` above has nothing to take as its argument until this is added. Non-generic (`public final class AggregateBuilder`, no `<T>`) — matching `GroupByBuilder`/`PipelineBuilder`/`SimilarBuilder`/`ChunksBuilder`, none of which are generic either. Mirrors `GroupByBuilder.java`'s method names/shape exactly:

```java
public AggregateBuilder(String typeName)
public AggregateBuilder where(String field, SearchOperator op, Object value)
public AggregateBuilder not(String field, SearchOperator op, Object value)
public AggregateBuilder withLogic(SearchLogic logic)
public AggregateBuilder having(String alias, SearchOperator op, Object value)
public AggregateBuilder withHavingLogic(SearchLogic logic)
public AggregateBuilder join(String leftField, String rightType, String rightField, JoinKind kind)
public AggregateBuilder terms(String field, String name, int size)
public AggregateBuilder dateHistogram(String field, String name, String calendarInterval, String timeZone)
public AggregateBuilder range(String field, String name, List<RangeBucket> buckets)
public AggregateBuilder avg(String field, String name)
public AggregateBuilder sum(String field, String name)
public AggregateBuilder min(String field, String name)
public AggregateBuilder max(String field, String name)
public AggregateBuilder count(String field, String name)
public AggregateBuilder countAll(String name)
public AggregateRequest build(String traceId)
```

Same field mapping and same `group_by_fields`/`expression`-override omission as DotNet's `AggregateBuilder` (see the DotNet section for the full rationale — not repeated per language).

No existing `EntityCoordinatorTest.java` exists to extend (only builder tests exist today, same situation as Go) — new tests establish this file following the mocking convention already used in `SchemaRegistrarTest.java`, plus a new `AggregateBuilderTest.java` matching `GroupByBuilderTest.java`'s existing shape.

---

## Python

Python's channel construction (`IversonClient.__init__`, `core.py:360-385`) already composes multiple credential sources onto one channel via `grpc.composite_channel_credentials(channel_credentials, *call_credentials)` — confirmed this accepts more than one `call_credentials` argument directly. This is Python's own idiomatic equivalent of Go/Java's transport-credentials layer, so the acting-user token fits into the *same* existing mechanism:

- `IversonClient.__init__` gains an `acting_user_token: str | None` keyword param. When set, a second `grpc.metadata_call_credentials(_ActingUserAuthPlugin(token))` (a new small plugin mirroring the existing `_BearerTokenAuthPlugin`, emitting `x-acting-user-authorization` instead of `authorization`) is composed into the same credentials chain. Every stub built from `self._channel` — present and future — gets it automatically.
- The channel-construction branch condition changes from `if credentials is not None:` to `if credentials is not None or acting_user_token is not None:` — otherwise setting only `acting_user_token` (no base `credentials`) would silently fall through to `grpc.insecure_channel` and drop the token. (Found during verification; a mechanical fix, not a shape change.)
- `EntityCoordinator.__init__` gains a fourth stub, `self._search = search_grpc.ObjectSearchServiceStub(channel)`.
- **New: `AggregateBuilder`** (`iverson_client/aggregate.py`) — no `AggregateRequest` builder exists in Python today (confirmed: `find . -iname "*aggregate*"` under `iverson_client` returns nothing); `aggregate` below has nothing to take as its argument until this is added. Mirrors `group_by.py`'s `GroupByBuilder` method names/shape exactly: `where`/`not_`/`with_logic` (query filter), `having`/`with_having_logic` (post-filter), `join`, `terms`/`date_histogram`/`range`/`avg`/`sum`/`min`/`max`/`count`/`count_all` (one `AggregationSpec` each, same `AggregationType` mapping as DotNet's `AggregateBuilder` — see that section for the full rationale), `build(trace_id="")`. Same `group_by_fields`/`expression`-override omission as the other languages' new builders.
- Six new methods (`search`, `search_similar`, `search_chunks`, `group_by`, `aggregate`, `pipeline`), each taking the already-built request from the existing builders (`search.py`/`group_by.py`/`pipeline.py`/`vector_search.py`, plus the new `aggregate.py` above). `search`/`search_similar` iterate the streaming stub call and convert each row via the existing `_from_struct` pattern (genuinely `T`-shaped results). `group_by`/`pipeline` iterate the same way but convert via a new untyped `_struct_to_dict` module-level function (not `self._cls`-bound, unlike `_from_struct`) — their columns are aggregated/aliased and don't match `T`'s own fields, so `_from_struct` would silently drop most of them. `search_chunks` iterates and returns the flat messages as-is. `aggregate` is a single call returning `AggregateResponse` as-is.

New tests follow the mocking convention already used in the builder tests (`test_group_by.py`, etc.) — Python also has no existing `EntityCoordinator`-level execution test today — plus a new `test_aggregate.py` matching `test_group_by.py`'s existing shape.

---

## TypeScript

Reproduced from `docs/specs/2026-07-22-mcp-server-design.md` Phase A (unchanged; that document's copy is now considered superseded by this one):

- `@iverson/client`'s `package.json` gets `main`/`types`/`exports` fields and a `"build": "tsc"` script — it currently has none, so nothing can depend on it as a package (only relative-source imports within its own folder work today).
- `IversonClient`'s constructor gains an optional acting-user token source (a string or a `() => Promise<string>` provider). Every call site — the existing `persist`/`update`/`delete`/`get`/`getMany` and the six new search-family methods — merges this into the call's metadata via the existing (but currently unexported) `createActingUserMetadata` helper.
- Six new `IversonClient` methods (`search`, `groupBy`, `pipeline`, `searchSimilar`, `aggregate`, `searchChunks`), executed against a new `ObjectSearchServiceClient` field. `search`/`groupBy`/`pipeline`/`searchSimilar` share one Struct-conversion helper reusing the existing `payloadToEntity`, each taking an *optional* entity class for typed conversion — `search`/`searchSimilar` are called with one (genuinely `T`-shaped results); `groupBy`/`pipeline` are called without one and get plain `Record<string, unknown>` back, since their columns are aggregated/aliased and don't match any entity's own fields (this qualifier was dropped when this section was originally reproduced from `docs/specs/2026-07-22-mcp-server-design.md` Phase A — restored here per CDR round 1 finding §2.3). `aggregate`/`searchChunks` are typed pass-throughs.
- **New: `AggregateBuilder`** (`src/aggregate.ts`) — no `AggregateRequest` builder exists in TypeScript today (confirmed: `find . -iname "*aggregate*"` under `src`/`sample`/`tests` returns nothing); the new `aggregate` method above has nothing to take as its argument until this is added. Mirrors `group-by.ts`'s `GroupByBuilder` method names/shape exactly (camelCase, `this`-returning chain): `where`/`not`/`withLogic` (query filter), `having`/`withHavingLogic` (post-filter), `join`, `terms`/`dateHistogram`/`range`/`avg`/`sum`/`min`/`max`/`count`/`countAll` (one `AggregationSpec` each, same `AggregationType` mapping as DotNet's `AggregateBuilder` — see that section for the full rationale), `build(traceId = '')`. Same `group_by_fields`/`expression`-override omission as the other languages' new builders. Exported from `index.ts` alongside `GroupByBuilder`.
- `createOAuth2ClientCredentials`/`createActingUserMetadata` (already implemented in `auth.ts`) get added to `index.ts`'s public exports.
- `SchemaRegistrar._buildRequest`'s entity-reflection logic gets promoted into a small shared helper (used by both `SchemaRegistrar` and, later, the MCP server's entity loader).

---

## Testing & scope boundaries

**Testing:** each new method gets a unit test mocking the gRPC stub, following that language's own established convention. DotNet extends its existing per-method `EntityCoordinator` test files. Go, Java, Python, and TypeScript have no existing `EntityCoordinator`-level execution tests at all today (only builder tests) — new tests establish that pattern per language, following each language's builder-test mocking style as the closest precedent. Each new `AggregateBuilder` also gets its own builder-level test file, matching that language's existing `GroupByBuilder` test file exactly (e.g. `GroupByBuilderTest.java` → `AggregateBuilderTest.java`).

**Error handling:** unchanged from each language's existing convention (DotNet: log + null/empty on failure; Go: `fmt.Errorf` wrapping; Java: `StatusRuntimeException`; Python: `RuntimeError`; TypeScript: thrown `Error`). No new error-handling pattern introduced anywhere.

**Out of scope:**
- Retrofitting acting-user support onto already-working methods (DotNet's 4 existing search methods; any language's `persist`/`get`/`update`/`delete`).
- Any change to the generated proto clients or `.proto` files themselves.
- The MCP server itself (`Iverson.Clients/McpServer/`) — this design closes its prerequisite; that work resumes per `docs/specs/2026-07-22-mcp-server-design.md` Phase B once this lands.

---

## Verified assumptions

All verified against the current repository state before this spec was finalized:

| # | Assumption | Evidence |
|---|---|---|
| 1 | DotNet's generated `ObjectSearchServiceClient` has `AggregateAsync(request, headers=null, ...) → AsyncUnaryCall<AggregateResponse>` and `GroupBy(request, headers=null, ...) → AsyncServerStreamingCall<SearchResponse>` | `Iverson.Client.Contracts/obj/Debug/net10.0/ObjectSearchGrpc.cs:229-259` |
| 2 | `StructConverter.ToDictionary(Struct)` exists and is generic/reusable | `Iverson.Client.Core/StructConverter.cs:50-51` |
| 3 | Go's generated `ObjectSearchServiceClient` interface matches exactly: streaming for Search/SearchSimilar/SearchChunks/GroupBy/Pipeline, direct `(*AggregateResponse, error)` for Aggregate | `Go/generated/object_search_grpc.pb.go:35-42` |
| 4 | No existing Go test constructs `coordinatorDeps{}` or calls `newEntityCoordinatorWithDeps` — adding a field to `coordinatorDeps` breaks nothing | `grep -rn "coordinatorDeps{" Go/iverson*/*.go` — one hit, inside `coordinator.go` itself; `find . -iname "coordinator*test*"` — no results |
| 5 | `AbstractStub.withOption(CallOptions.Key<T>, T)` is a real, public, final grpc-java API returning a new stub instance | grpc-java javadoc (`io.grpc.stub.AbstractStub`) |
| 6 | Java's generated stub for the 5 new RPCs follows the same deterministic shape as the already-working `search()` (`Iterator<SearchResponse> search(SearchRequest)`) — by analogy, since generated sources aren't present pre-build | `.proto` RPC definitions (`object_search.proto`) + `EntityCoordinator.java:133-135`'s working `search()` call |
| 7 | Python's generated stub: `Search`/`SearchSimilar`/`SearchChunks`/`GroupBy`/`Pipeline` are `channel.unary_stream`; `Aggregate` is `channel.unary_unary` | `iverson_client/generated/object_search_pb2_grpc.py:38-64` |
| 8 | `grpc.composite_channel_credentials(channel_credentials, *call_credentials)` accepts multiple call-credentials in one call | grpc-python documentation |
| 9 | Python's channel construction only enters the secure/composite-credentials path when `credentials is not None` — setting only `acting_user_token` would silently drop it without a fix | Full read of `core.py:360-385` |
| 10 | No interface/ABC/protocol wraps `EntityCoordinator` in any of the 4 non-TypeScript languages — new methods are safe, non-breaking additions | `grep` for interface/protocol wrappers across all 4 client source trees — no hits |
| 11 | No client-side request-builder for `AggregateRequest` exists in any of the 5 languages (found via CDR round 1's span check — not covered by the original assumption list, despite being load-bearing for 5 of the 6 per-language sections) | `find . -iname "*aggregate*"` across `Iverson.Clients/{DotNet,Go,Java,Python,TypeScript}` — no hits outside Java's unrelated `AggregationType` enum usage in `PipelineStepBuilder.java` |
| 12 | Java's `GroupByBuilder`/`SimilarBuilder`/`ChunksBuilder` are non-generic (`public final class`, no `<T>`) — only `QueryBuilder<T>` is generic (found via CDR round 1's span check) | `GroupByBuilder.java:42`, `SimilarBuilder.java:17`, `ChunksBuilder.java:12` |

Also inherited as ground truth from the MCP server design's own verification (not re-verified here): TypeScript's specific findings (`package.json` consumability, `callUnary`'s hardcoded metadata, `ObjectSearchServiceClient`'s generated shape, `payloadToEntity`'s reusability).
