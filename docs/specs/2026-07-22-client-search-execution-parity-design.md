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

`headers` threads through exactly like `PersistAsync` already does. Nothing else in `Iverson.Client.Core` changes — the 4 already-working search methods and the CRUD methods are untouched. New tests: `EntityCoordinatorGroupByTests.cs` / `EntityCoordinatorAggregateTests.cs`, matching the existing `EntityCoordinatorPipelineTests.cs` pattern.

---

## Go

Six new methods on `EntityCoordinator[T]` (`Go/iverson/coordinator.go`), following the existing adapter-interface pattern used for `PersistenceClient`/`RetrievalClient`/`MappingDeleteClient`:

- New `SearchClient` interface (`Search`, `SearchSimilar`, `SearchChunks`, `GroupBy`, `Aggregate`, `Pipeline`) + a `searchAdapter` wrapping `pb.ObjectSearchServiceClient`, added to `coordinatorDeps` and wired in `NewEntityCoordinator` — mirrors `persistenceAdapter`/`retrievalAdapter`/`mappingDeleteAdapter`.
- `Search`, `SearchSimilar`, `GroupBy`, `Pipeline` stream `*pb.SearchResponse` (a new `SearchStream` interface plays the same role as the existing `RetrievalStream`), converting each row with the existing `structToEntity[T]` helper.
- `SearchChunks` streams `*pb.ChunkSearchResponse` — same loop shape, no conversion.
- `Aggregate` is a single call, returns `*pb.AggregateResponse` as-is (Go's generated client already returns this directly, not via callback).

All six take `ctx context.Context` as their first parameter, matching every existing method. A caller attaches the acting-user token via the already-existing `iverson.WithActingUserToken(ctx, token)` before calling — no changes needed to `auth.go` or the credentials layer.

New tests follow the mock-injection scaffolding already present (`newEntityCoordinatorWithDeps`) — currently unused by any test, since Go has no existing `EntityCoordinator`-level test file at all (only builder tests exist today).

---

## Java

Five new methods on `EntityCoordinator<T>` (`Java/client/src/main/java/io/iverson/client/core/EntityCoordinator.java`):

```java
public List<Map<String, Object>> groupBy(GroupByBuilder<T> builder, String actingUserToken)
public AggregateResponse aggregate(AggregateBuilder<T> builder, String actingUserToken)
public List<Map<String, Object>> pipeline(PipelineBuilder builder, String actingUserToken)
public List<SearchResult<T>> searchSimilar(SimilarBuilder<T> builder, String actingUserToken)
public List<ChunkSearchResult> searchChunks(ChunksBuilder<T> builder, String actingUserToken)
```

Each also gets a no-token overload delegating with `null`. `groupBy`/`pipeline` return untyped `List<Map<String, Object>>` (dynamic column set). `aggregate` returns the typed `AggregateResponse` as-is. `searchSimilar` reuses the existing `SearchResult<T>` record. `searchChunks` returns a new small `ChunkSearchResult` record (`parent_key`/`chunk_text`/`score`) — Java has no existing equivalent to reuse, unlike DotNet's generated `ChunkSearchResponse`.

When `actingUserToken` is non-null, the call goes through `client.searchStub.withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, actingUserToken)` — the mechanism `applyRequestMetadata` already reads (`OAuth2ClientCredentials.java:56-59`), exercised from `EntityCoordinator` for the first time. `search()` itself is unchanged.

No existing `EntityCoordinatorTest.java` exists to extend (only builder tests exist today, same situation as Go) — new tests establish this file following the mocking convention already used in `SchemaRegistrarTest.java`.

---

## Python

Python's channel construction (`IversonClient.__init__`, `core.py:360-385`) already composes multiple credential sources onto one channel via `grpc.composite_channel_credentials(channel_credentials, *call_credentials)` — confirmed this accepts more than one `call_credentials` argument directly. This is Python's own idiomatic equivalent of Go/Java's transport-credentials layer, so the acting-user token fits into the *same* existing mechanism:

- `IversonClient.__init__` gains an `acting_user_token: str | None` keyword param. When set, a second `grpc.metadata_call_credentials(_ActingUserAuthPlugin(token))` (a new small plugin mirroring the existing `_BearerTokenAuthPlugin`, emitting `x-acting-user-authorization` instead of `authorization`) is composed into the same credentials chain. Every stub built from `self._channel` — present and future — gets it automatically.
- The channel-construction branch condition changes from `if credentials is not None:` to `if credentials is not None or acting_user_token is not None:` — otherwise setting only `acting_user_token` (no base `credentials`) would silently fall through to `grpc.insecure_channel` and drop the token. (Found during verification; a mechanical fix, not a shape change.)
- `EntityCoordinator.__init__` gains a fourth stub, `self._search = search_grpc.ObjectSearchServiceStub(channel)`.
- Six new methods (`search`, `search_similar`, `search_chunks`, `group_by`, `aggregate`, `pipeline`), each taking the already-built request from the existing builders (`search.py`/`group_by.py`/`pipeline.py`/`vector_search.py` — unchanged). `search`/`search_similar`/`group_by`/`pipeline` iterate the streaming stub call and convert each row via the existing `_from_struct` pattern. `search_chunks` iterates and returns the flat messages as-is. `aggregate` is a single call returning `AggregateResponse` as-is.

New tests follow the mocking convention already used in the builder tests (`test_group_by.py`, etc.) — Python also has no existing `EntityCoordinator`-level execution test today.

---

## TypeScript

Reproduced from `docs/specs/2026-07-22-mcp-server-design.md` Phase A (unchanged; that document's copy is now considered superseded by this one):

- `@iverson/client`'s `package.json` gets `main`/`types`/`exports` fields and a `"build": "tsc"` script — it currently has none, so nothing can depend on it as a package (only relative-source imports within its own folder work today).
- `IversonClient`'s constructor gains an optional acting-user token source (a string or a `() => Promise<string>` provider). Every call site — the existing `persist`/`update`/`delete`/`get`/`getMany` and the six new search-family methods — merges this into the call's metadata via the existing (but currently unexported) `createActingUserMetadata` helper.
- Six new `IversonClient` methods (`search`, `groupBy`, `pipeline`, `searchSimilar`, `aggregate`, `searchChunks`), executed against a new `ObjectSearchServiceClient` field. `search`/`groupBy`/`pipeline`/`searchSimilar` share one Struct-conversion helper reusing the existing `payloadToEntity`; `aggregate`/`searchChunks` are typed pass-throughs.
- `createOAuth2ClientCredentials`/`createActingUserMetadata` (already implemented in `auth.ts`) get added to `index.ts`'s public exports.
- `SchemaRegistrar._buildRequest`'s entity-reflection logic gets promoted into a small shared helper (used by both `SchemaRegistrar` and, later, the MCP server's entity loader).

---

## Testing & scope boundaries

**Testing:** each new method gets a unit test mocking the gRPC stub, following that language's own established convention. DotNet extends its existing per-method `EntityCoordinator` test files. Go, Java, Python, and TypeScript have no existing `EntityCoordinator`-level execution tests at all today (only builder tests) — new tests establish that pattern per language, following each language's builder-test mocking style as the closest precedent.

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

Also inherited as ground truth from the MCP server design's own verification (not re-verified here): TypeScript's specific findings (`package.json` consumability, `callUnary`'s hardcoded metadata, `ObjectSearchServiceClient`'s generated shape, `payloadToEntity`'s reusability).
