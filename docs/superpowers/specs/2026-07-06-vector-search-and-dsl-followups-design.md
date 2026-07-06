# Vector-Search DSL + DSL Follow-ups — Design

**Date:** 2026-07-06
**Status:** Approved, ready for implementation planning

## Problem

Two independent gaps, bundled into one plan by request:

1. **Qdrant vector search has no DSL.** `SearchSimilar`/`SearchChunks` (the RPCs the DSL-improvements
   plan declared "the real vector path" when it stripped `VECTOR_SIMILAR` out of the SQL-facing
   builders) have no fluent client builder in any of the 5 languages — callers hand-construct
   `SearchSimilarRequest`/`SearchChunksRequest` directly. They also carry no filter at all:
   `QdrantVectorService.SearchNamedAsync` never passes a `Filter` to the underlying Qdrant SDK call.
   Vector search and scalar filtering are today two fully disjoint paths.

   **Correction discovered during implementation planning (superseding an inaccurate claim in the
   original draft of this section):** it is not just that filtering is unwired — the payload data
   filtering would need doesn't fully exist yet either. `SchemaBuilder.ToCollectionSchema` declares
   a Qdrant payload index per scalar/FK column, but the actual ingestion path
   (`IntelligenceStoreConsumer.HandleAsync`) never writes those columns' values into the payload —
   it only writes `key` and the embedded field's own text. Separately, `IVectorService.UpsertAsync`/
   `UpsertNamedAsync` only accept `IReadOnlyDictionary<string, string>` payloads, so every value that
   *is* written is coerced to a Qdrant string-typed value regardless of its real type — which means
   Qdrant's `Range` condition (needed for `GREATER_THAN`/`LESS_THAN`) could never match against them
   even if the values were populated, since `Range` requires numeric-typed stored values. Part A now
   includes fixing both: typing the payload properly (`IVectorService`'s upsert payload parameter
   becomes value-preserving, `QdrantVectorService` writes typed Qdrant `Value`s) and populating every
   scalar/FK column's real value at ingestion time, not just the embedded field's own text.

2. **A backlog of Minor-severity findings and two cross-language divergences** were deliberately left
   unaddressed across the three most recently completed DSL plans (server pipeline, client pipeline,
   DSL-improvements) — see `docs/superpowers/plans/2026-07-05-pipeline-server.md`,
   `2026-07-05-pipeline-clients.md`, `2026-07-05-dsl-improvements.md`, and the memory record
   `project-pipeline-aggregations-design` for the full annotated list. This plan folds all of them in
   as concrete tasks so they don't keep aging as "not yet acted on."

## Part A: Qdrant vector-search DSL

### Proto changes (`Iverson.Clients/Common/Proto/object_search.proto`)

Add two fields to both `SearchSimilarRequest` and `SearchChunksRequest`, reusing the existing
`SearchClause`/`SearchLogic` types (same flat-field idiom as `PipelineRequest.base_where`/`base_logic`,
not a nested `SearchQuery` — its `sort` field wouldn't apply here since ranking is always by vector
score):

```proto
message SearchSimilarRequest {
    string type_name = 1;
    string property  = 2;
    string query      = 3;
    uint32 top_k       = 4;
    string trace_id    = 5;
    repeated SearchClause filter        = 6;  // new
    SearchLogic            filter_logic = 7;  // new
}
// SearchChunksRequest gets the identical two new fields, same numbers.
```

No new enums or messages. `VECTOR_SIMILAR` stays rejected on the SQL paths (`BuildWhere`/`BuildHaving`)
— unchanged, this plan doesn't touch that.

### Server changes

**New component — `QdrantFilterBuilder`** (new file,
`Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs`): translates a `SearchClause` list +
`SearchLogic` into a Qdrant `Filter`:

- `EQUALS` / `NOT_EQUALS` → `Match` condition (negation via `Filter.MustNot` grouping, driven by
  `NOT_EQUALS` or `SearchClauseType.MUST_NOT`)
- `GREATER_THAN` / `LESS_THAN` / `_OR_EQUALS` variants → `Range` condition
- `IN` → `Match` against multiple values ("match any")
- `SearchLogic.AND` / `OR` → `Filter.Must` / `Filter.Should` grouping
- `CONTAINS` / `STARTS_WITH` / `ENDS_WITH` / `VECTOR_SIMILAR` → **rejected**,
  `RpcException(InvalidArgument)` naming the operator and the RPC — no silent skipping, matching the
  precedent set by the SQL-path `VECTOR_SIMILAR` rejection.

**`IVectorService.SearchNamedAsync`** gains an optional `Filter? filter = null` parameter.
`QdrantVectorService.SearchNamedAsync` passes it straight through to the existing
`QdrantClient.SearchAsync(..., filter: filter, ...)` overload (already supported by the SDK, simply
never wired up).

**Payload typing and population** (new — see the correction note above):

- `IVectorService.UpsertAsync`/`UpsertNamedAsync` change their payload parameter from
  `IReadOnlyDictionary<string, string>?` to `IReadOnlyDictionary<string, object>?`.
  `QdrantVectorService` writes each value through a typed conversion to `Qdrant.Client.Grpc.Value`
  (string/long/double/bool via the SDK's own implicit operators; `DateTime`/`DateTimeOffset` stay
  ISO-8601 strings, matching `SearchValueConverter`'s existing convention on the client side) instead
  of coercing everything to a string. Read-side payload mapping
  (`VectorSearchResult.Payload`/`SearchNamedAsync`'s result projection) is unchanged — this is a
  write-path-only fix, scoped to what filtering correctness needs.
- `IntelligenceStoreConsumer.HandleAsync`'s named-vector upsert block additionally writes every
  `schema.ScalarColumns`/`schema.FkColumns` entry's real value (extracted from the event's JSON
  payload by `JsonValueKind`, not just as a string) into `pointPayload`, camelCased the same way the
  existing `key`/embedded-field-text entries already are.
- `SchemaBuilder.ToCollectionSchema`'s payload-index field names change from the raw PascalCase
  `ColumnDescriptor.Name` to the same camelCase form, so the declared index actually matches the
  payload keys now being written (a latent, previously-harmless naming mismatch — Qdrant filtering
  works without a matching index, just unoptimized, so this was invisible until filtering existed).

**`ObjectSearchGrpcService` changes:**

- `SearchSimilar`: validates each filter clause's `property` against the schema's known scalar/FK
  columns (same style as existing property-resolution checks elsewhere), builds the filter via
  `QdrantFilterBuilder`, passes it to `SearchNamedAsync`. Full filter expressiveness — the main
  collection's payload already indexes every scalar/FK column (`SchemaBuilder.ToCollectionSchema`).
- `SearchChunks`: the chunks collection's payload only carries `text`/`parent_id` — no other scalar
  columns are indexed there. Filter clauses are restricted to **at most one `EQUALS` clause on the
  type's primary-key property**; anything else (extra clauses, other operators, other properties)
  throws `InvalidArgument` naming the restriction. That clause maps to a `Match` condition on the
  `parent_id` payload key.

### Client changes (all 5 languages)

One canonical builder entry point per language — deliberately not repeating the two-entry-point
duplication already flagged as a nit on `Pipeline.For`/`Query.Pipeline`:

```csharp
Query.Similar<Article>(a => a.Title)
    .Text("machine learning")
    .TopK(10)
    .Where(a => a.Category, SearchOperators.Equals, "Tech")   // optional
    .Build();  // -> SearchSimilarRequest

Query.Chunks<Article>(a => a.Body)
    .Text("neural networks")
    .TopK(5)
    .Where(a => a.Id, SearchOperators.Equals, parentId)        // at most one, PK-equals only
    .Build();  // -> SearchChunksRequest
```

Other 4 languages mirror this idiomatically (`Query.similar`/`query.similar`/`Query.Similar`/etc.,
matching each language's existing `Query`/`GroupByBuilder` naming conventions).

**Build-time validation** (client-side, mirroring the "silent-drop builds become build-time errors"
precedent from the DSL-improvements plan — fail at `.Build()` before the server ever sees the
request):

- Both builders reject `CONTAINS`/`STARTS_WITH`/`ENDS_WITH`/`VECTOR_SIMILAR` in `.Where(...)` with a
  build-time, language-idiomatic error.
- The Chunks builder additionally rejects any `.Where(...)` clause that isn't a single `EQUALS` on
  the declared primary-key property.
- Server-side validation remains the authoritative backstop (defense in depth, same as every other
  builder in this DSL).

### Testing

- **Server:** `QdrantFilterBuilder` unit tests — one per supported operator (correct `Condition`
  shape), AND/OR grouping, `MUST_NOT`, and rejection tests for every unsupported operator asserting
  message content (not just exception type — applying the DSL-improvements review's own finding
  about message-content assertions to this code from day one). `ObjectSearchGrpcService` tests:
  `SearchSimilar` with a filter passes the expected `Filter` to a mocked `IVectorService`; unknown
  filter property → `InvalidArgument`; `SearchChunks` accepts a PK-equals clause and rejects anything
  else. `QdrantVectorService`/`IntelligenceStoreConsumer` tests updated for the payload-typing change
  (existing `Dictionary<string,string>` payload assertions become type-preserving); new
  `IntelligenceStoreConsumer` test confirming scalar/FK columns are written into the payload. A new
  Docker-backed integration test (mirrors `PipelineIntegrationTests`/`StarRocksIntegrationTests`
  conventions) against live Qdrant + live embedding: upsert points with distinct typed payload
  values, assert a filtered `SearchSimilar` (including a `GREATER_THAN`/`LESS_THAN` range clause,
  now meaningful since payload values are typed) returns only matching points; same pattern scoped
  to one parent for `SearchChunks`.
- **Clients (all 5 languages):** builder unit tests — happy-path proto shape (filter/filter_logic
  populated correctly), build-time rejection of unsupported operators, build-time rejection of
  non-PK-equals clauses on the Chunks builder.
- **Docs:** new "Vector search: SearchSimilar/SearchChunks builders" section in
  `docs/one-query-five-languages.md`, per-language snippets fact-checked against the merged code
  (same process used for the Pipelines doc section).

## Part B: Join equalization (client-only, no proto changes)

Two divergences flagged in the pipeline-client and DSL-improvements reviews, both closeable without
touching the wire format:

**B1. Pipeline composite-key joins → Java/Python/TypeScript/Go.** `PipelineJoin.on` is already
`repeated JoinCondition` — C#'s `PipelineStepBuilder.Join(string source, IReadOnlyList<(string Left,
string Right)> on, JoinKind kind = Inner)` (`PipelineBuilder.cs:235-243`) already loop-appends one
`JoinCondition` per pair. Add the equivalent overload to the other 4 languages' `PipelineStepBuilder`,
language-idiomatic shape (Java: `List<String[]>` or a small pair type; Python: `List[Tuple[str,str]]`;
TypeScript: `Array<{left: string, right: string}>`; Go: `[]JoinCondition`). Existing single-pair
overloads stay for the common case.

**B2. Plain-Search multi-hop joins → Java/Python/TypeScript/Go.** `JoinSpec.left_type` is already a
free string on the wire — C#'s `QueryBuilder<T>.Join<TLeft,TRight>(...)` (`QueryBuilder.cs:99-115`)
already sets an explicit left type via a generic parameter. The other 4 languages lack C#'s generics
mechanism, so add an overload taking the left type as an explicit string:
`join(leftType, leftField, rightType, rightField[, kind])`, alongside the existing shorter overload
that defaults left type to the query's own base type.

**Explicitly excluded:** composite-key joins for plain Search/GroupBy (`JoinSpec`) — that message has
only one `left_field`/`right_field` pair on the wire, unlike `PipelineJoin`. This is a symmetric gap
(no language has it, including C#), not a cross-language divergence, and needs its own proto change.
Tracked separately in memory (`project-joinspec-composite-key-followup`) as a future plan, not part of
this one.

**Testing:** unit tests per language mirroring C#'s `Join_MultiHop_SetsExplicitLeftType` (multi-hop)
and the existing Pipeline composite-key test pattern (`PipelineBuilderTests.cs`) — happy-path proto
shape assertions for both new overloads, all 4 languages.

**Docs:** update wherever `docs/one-query-five-languages.md` documents join capability so the
composite-key/multi-hop capability table no longer shows these as C#-only.

## Part C: Minor findings from the three prior DSL plans

Pulled from `project-pipeline-aggregations-design` (memory) and the three plan files' own
Deferred/Minor-findings sections. Grouped by treatment:

### C1. Test-coverage gaps (add tests, no behavior change)

1. `EmitSelectItem`'s untested `All=true`-from-non-input-join-source branch (server pipeline plan).
2. No case-mismatch join-source-name test — works via `OrdinalIgnoreCase`, untested (server pipeline
   plan).
3. `StarRocksNotReadyException`→`Unavailable` untested in the `Pipeline` method (server pipeline
   plan; pre-existing gap shared with `GroupBy`, not new).
4. `TraceId` propagation unasserted in the streaming test (server pipeline plan).
5. Pipeline validation-failure tests use loose substring assertions instead of full message
   assertions (server pipeline plan).
6. `BuildHaving`'s `VECTOR_SIMILAR`-rejection test asserts status code only, not message content,
   unlike its sibling `BuildWhere`/`BuildSearch` tests (DSL-improvements plan).
7. `EntityCoordinator.PipelineAsync`/`PipelineAsync<TResult>` have zero automated test coverage —
   only exercised by the C# sample app (client pipeline plan).
8. Canonical validation rules 1 (empty-name/reserved-`base`) and 4 (metrics-without-groupBy) are
   under-tested in several languages, though implemented correctly everywhere (client pipeline plan).
9. Case-insensitive `GroupByBuilder` validation paths have zero test coverage in any of the 5 suites
   (DSL-improvements plan).
10. 3 integration tests (`TopNPerGroup`, `JoinCteAgainstBase`, `DerivedRatio`) assert counts/sums
    rather than specific row identities/values — `TopNPerGroup` is the weakest since it's the
    flagship windowing scenario (server pipeline plan).

### C2. Small hardening fixes (real, narrow behavior change)

11. `MetricSpec.Name` gains identifier validation, matching the existing validation on
    window/derive/select aliases (server pipeline plan — currently asymmetric but safe).
12. Derive-expression validator blocks SQL comment sequences `--` and `/* */` in addition to the
    already-blocked `; ' \`` (server pipeline plan — can't inject logic today, but can truncate a
    query into a raw SQL error instead of a clean rejection).
13. Key/metric-alias name collision detection (fail-fast) added to `GroupByBuilder` validation, all
    5 languages (DSL-improvements plan — currently silently allowed, consistent by design but a
    real gap worth closing).

### C3. DRY/cosmetic cleanups (no behavior change)

14. Dedupe the schema→column-dict loop duplicated between `TrackAndValidate` and
    `ResolveJoinSources` in `StarRocksPipelineBuilder.cs` (server pipeline plan).
15. Java `PipelineStepBuilder.count(field)` inlines `"_count"` instead of delegating to its own
    2-arg overload the way `GroupByBuilder.count` does (client pipeline plan).
16. Java `GroupByBuilder.java`'s validation block uses inline fully-qualified type names instead of
    imports (DSL-improvements plan).
17. `docs/one-query-five-languages.md`'s "step toolbox" table over-attributes `derive()`'s
    expression validation to the client builder when it's actually server-side in all 5 languages
    (client pipeline plan — the doc's separate "two-layered validation" note elsewhere already says
    this correctly; just one wrong table cell).

### C4. API-shape consolidation

18. C# has two Pipeline entry points (`Pipeline.For` + `Query.Pipeline`) from a genuine
    self-contradiction in the originating task brief — both work today, harmless duplication.
    Consolidate to one canonical form (client pipeline plan). Same principle already applied to the
    new `Query.Similar`/`Query.Chunks` builders in Part A — pick `Query.Pipeline` as the canonical
    form (matches the naming chosen for the new vector-search builders) and remove `Pipeline.For`.

## Out of scope

- Composite-key joins for plain Search/GroupBy (`JoinSpec`) — tracked separately, see Part B.
- Any change to the SQL-path `VECTOR_SIMILAR` rejection (`BuildWhere`/`BuildHaving`) — stays exactly
  as the DSL-improvements plan left it.
- Payload-index expansion on the chunks collection (would be required to support richer-than-PK
  filtering on `SearchChunks`) — not needed given the PK-only scope chosen for Part A.

## Testing summary (all parts)

Every task above gets tests as part of the task, following the existing per-language/per-surface
conventions established across the three prior plans (server unit + Docker-backed integration tests
where applicable, per-language builder unit tests for all 5 languages, docs fact-checked against
merged code). No separate "testing phase" — testing is inline with each task, matching how the three
prior plans were structured.
