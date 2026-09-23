# Row pattern matching (`MatchPattern`) across StarRocks and Qdrant

## Motivation

SQL:2016 row pattern recognition (`MATCH_RECOGNIZE`) finds regular-expression patterns over an
ordered sequence of rows, where each pattern variable's "letter" is decided by a `DEFINE`
predicate. Iverson has no equivalent. The existing `Pipeline` RPC can express only fixed-length,
gap-free patterns (via `LAG`/`LEAD` window functions plus a `where`); it cannot express
quantifiers, alternation, or predicates that reference the match so far.

No single use case is committed. Three are in scope, and this design serves all three with one
engine:

1. **Event / engagement sequences** — ordered rows of a registered type in StarRocks
   ("viewed, then saved, then cited within 7 days").
2. **Semantic sequences** — ordered rows where some `DEFINE` predicates are vector similarity
   ("a message about refunds, then one or more that are not, then one about cancellation").
3. **Within-document chunk order** — patterns over one document's chunk sequence, which lives
   only in Qdrant ("a section about X followed by a section about Y").

None of the three stores can run such a pattern: StarRocks has no `MATCH_RECOGNIZE` (only
`window_funnel()`, a restricted ordered-steps function), released Postgres has none, and Qdrant
has no notion of row order. The matcher therefore runs in the Iverson server, fed by the stores.

## Explored and deliberately not pursued

- **Trino as a sidecar query engine** (translate to Trino `MATCH_RECOGNIZE`): rejected. Upstream
  Trino has no StarRocks connector (trinodb/trino issue #17329; only third-party forks) and no
  vector-database connector, so use case 3 is impossible and similarity would have to be shipped
  in as inline `VALUES`. Row/field authorization and tenant isolation would need re-implementing in
  Trino SQL, and it adds a ~1.04 GB JVM image to every deployment profile including laptop.
  Trino is used **only as a test oracle** (§9.2).
- **SQL pushdown into StarRocks** (compile patterns to self-joins, or to a regex over an ordered
  string aggregate of per-row labels): rejected. It cannot express the approved scope —
  history-dependent `DEFINE` predicates, `PERMUTE`, overlapping `SKIP TO` modes, empty matches — and
  the string-regex form is wrong whenever two `DEFINE` predicates can both hold for one row. It
  remains a possible later optimization for a simple subset; not part of this design.
- **NEsper** (a .NET CEP engine with `match_recognize`): rejected — GPL-licensed; Iverson is MIT.
- **A Postgres row source**: rejected. StarRocks already serves every query RPC
  (`Search`/`Aggregate`/`GroupBy`/`Pipeline`) over the same rows; Postgres serves only `Get`. A
  Postgres reader would duplicate the StarRocks one with no user-visible benefit.
- **Exact thresholded Qdrant search for similarity predicates**: replaced by retrieve-and-compute
  (§3.3). `IVectorQueryService.SearchNamedAsync` exposes neither `exact` nor `score_threshold`, and
  a boolean threshold would not give `SIMILARITY` a numeric value usable in `MEASURES`.

## Design

### 1. API

New RPC on `ObjectSearchService` in `Iverson.Clients/Common/Proto/object_search.proto`:

```proto
rpc MatchPattern (MatchPatternRequest) returns (stream MatchPatternResponse);

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

**Row shapes by source.**

- `TYPE_ROWS`: the rows of `type_name` in the caller's tenant StarRocks database. Rows are ordered
  by `partition_by`, then `order_by`, then the key column as a final tie-breaker (so ordering is
  deterministic). Omitting `partition_by` makes the whole filtered set one partition.
- `CHUNKS`: the chunks of `chunk_property` for `type_name`. Each row has the columns
  `parent_key` (string), `chunk_index` (integer) and `text` (string). The source is implicitly
  partitioned by `parent_key` (ordinal string order) and ordered by `chunk_index` (numeric).
  `partition_by` and `order_by` must be empty (`InvalidArgument` otherwise).

**Output shapes.**

- `ONE_ROW`: partition columns (for `CHUNKS`, `parent_key`) followed by the measures.
- `ALL_ROWS_*`: every input column visible to the caller, followed by the measures. For
  `TYPE_ROWS` that is the caller's full `ColumnsFor` set, minus bytes columns (§3.2). For
  `CHUNKS` it is the three chunk columns.
- The tenant column never appears in any output (§7).

**Matching semantics** are SQL:2016 `MATCH_RECOGNIZE` table semantics. A match is attempted at
every row that the previous match's `AFTER MATCH SKIP` did not skip. Among competing matches, the
preferred one wins by the standard's rules: leftmost start, then pattern preference order (greedy
or reluctant quantifier order, alternation left to right, `PERMUTE` lexicographic order).
`AFTER MATCH SKIP TO FIRST/LAST v` fails the request (`InvalidArgument`) when it would resume at
the first row of the current match, or when `v` bound no row.

**Pattern grammar** (`pattern`): concatenation; alternation `|`; grouping `( )`; the empty
pattern `()`; quantifiers `*` `+` `?` `{n}` `{n,}` `{,m}` `{n,m}`, each optionally followed by `?`
(reluctant); `PERMUTE(a, b, ...)`; exclusion `{- ... -}`; anchors `^` (partition start) and `$`
(partition end). Variable names are identifiers. A variable used in `pattern` but absent from
`define` is always true. A `define` or `subsets` entry naming a variable that the pattern never
uses is `InvalidArgument`. `subsets` names must not collide with pattern variables. `define`
entries must name distinct variables, and `subsets` names must be distinct. `measures` names must
be distinct, must differ (ordinal comparison) from every output column of the chosen shape (see
Output shapes above), and must not be the reserved tenant column (compared case-insensitively).
Otherwise the request is `InvalidArgument`. Distinctness of `measures`, `define` and `subsets`
names, the reserved-tenant-column check, and — for `CHUNKS` — the comparison against the chosen
shape's output columns (`parent_key` for `ONE_ROW`; `parent_key`, `chunk_index` and `text` for
`ALL_ROWS_*`) are enforced by `PatternQuery.Compile` (§3.4 step 2), which receives
`rows_per_match` among its request parts. For
`TYPE_ROWS` the output-column comparison is enforced by `MatchRowsAsync` (§3.2), which holds the
validated column set: it rejects a `measures` name equal (ordinal) to a canonical name in that set
for `ALL_ROWS_*`, or to the canonical name of a `partition_by` column for `ONE_ROW`, throwing
`EngagementQueryTranslationException` before the query runs. `ONE_ROW` emits partition columns
under those canonical names. Exclusion
combined with `ALL_ROWS_WITH_UNMATCHED` is `InvalidArgument`, as the standard requires.

### 2. Expression language

`define` (boolean) and `measures` (any type) share one grammar. Expressions are parsed and
evaluated in C#. **No expression, pattern, or subset string is ever spliced into SQL.**

- Literals: numbers, single-quoted strings (`''` escapes a quote), `TRUE`, `FALSE`, `NULL`.
- Column references: `col`, `VAR.col`, `SUBSET.col`. An unqualified `col` in `define` refers to
  the current row; in `measures` it refers to the universal row pattern variable (all rows of the
  match so far), per the standard.
- Operators: `+ - * / %`, unary `-`, `= <> < <= > >=`, `AND OR NOT`, `IS [NOT] NULL`,
  `[NOT] IN (...)`, `[NOT] BETWEEN ... AND ...`, and searched/simple `CASE`. Integer `/` integer
  truncates toward zero, as in Trino. Mixed integer and decimal arithmetic promotes to double.
  String comparison is ordinal.
- Navigation: `PREV(expr [, n])`, `NEXT(expr [, n])` (physical offsets, default 1),
  `FIRST(expr [, n])`, `LAST(expr [, n])` (logical offsets within the bound rows), and the
  standard's compound forms (`PREV(FIRST(A.x), 2)` and so on). A navigation that runs out of range
  yields `NULL`.
- Aggregates over bound rows: `COUNT(*)`, `COUNT(expr)`, `SUM`, `AVG`, `MIN`, `MAX`, each over
  a variable- or subset-qualified argument (`COUNT(A.*)`, `SUM(B.price)`). Navigation functions
  nested inside aggregates are `InvalidArgument`, as the standard requires.
- Match functions: `CLASSIFIER([var])` and `MATCH_NUMBER()`, both permitted in `define` and
  `measures`. Inside `define`, `MATCH_NUMBER()` is the number the match attempt in progress will
  receive.
- `FINAL` is permitted in `measures` only and is `InvalidArgument` inside `define`, as the
  standard requires. `RUNNING` is permitted in both and is the default; for `ONE_ROW`, `RUNNING`
  and `FINAL` are equivalent.
- Scalar functions: `COALESCE`, `NULLIF`, `ROUND`, `ABS` (the functions `Pipeline` already allows)
  and `TIMESTAMPDIFF(unit, a, b)`, where unit is `SECOND|MINUTE|HOUR|DAY`. It returns the whole
  number of units from `a` to `b`, truncated toward zero.
- `SIMILARITY(col, 'text')`: the cosine similarity between `col`'s stored vector and the query
  embedding of the literal `'text'`, as a double. For `TYPE_ROWS`, `col` must be a property of
  `type_name` with an embedding vector that is visible to the caller: `col` is resolved
  case-insensitively to a `schema.VectorFields` descriptor, as `SearchSimilar` does, and the vector
  name is `descriptor.PropertyName.ToSnakeCase() + "_vector"`. For
  `CHUNKS`, `col` must be `text` (the chunk's own vector). The second argument must be a non-empty
  string literal. A row whose point or vector does not exist yields `NULL`, so a `define`
  predicate over it is not satisfied (three-valued logic). A stored zero-magnitude vector yields
  IEEE `NaN`, which is kept as an ordinary double (see below).

Evaluation uses SQL three-valued logic for `NULL`. `NaN` is not `NULL`; it follows .NET `double`
semantics:
- every comparison (`= < <= > >=`, `IN`, `BETWEEN`) between `NaN` and a non-`NULL` value is
  `FALSE` and `<>` is `TRUE`, so `NOT` over such a comparison, `NOT IN` and `NOT BETWEEN` are
  `TRUE`; a comparison with a `NULL` operand stays `UNKNOWN`, as for any other value, and
  `IN`/`BETWEEN` are evaluated as their `=` / `>= AND <=` expansions under three-valued logic (so
  `NaN IN (0.5, NULL)` is `UNKNOWN`, while `NaN BETWEEN 0 AND NULL` is `FALSE`);
- `IS NULL` is `FALSE`;
- arithmetic, `ROUND`, `ABS`, `SUM` and `AVG` propagate `NaN`;
- `COUNT(expr)` counts it, and `COALESCE` and `NULLIF` return it;
- `MIN`/`MAX` use `double.CompareTo` ordering, where `NaN` sorts lowest, so `MIN` returns `NaN`
  and `MAX` ignores it unless every value is `NaN`;
- a `NaN` measure is emitted as a `number_value` of `NaN`.

A `define` predicate labels the row only when it
evaluates to `TRUE`. Evaluation errors (for example, division by zero, or a type mismatch detectable
only at run time) fail the request with `InvalidArgument`.

### 3. Components

#### 3.1 `Iverson.Server/Iverson.Patterns` (new project, no store dependencies)

Added to both `Iverson.slnx` and `Iverson.Server/Iverson.Server.slnx`, with a sibling
`Iverson.Patterns.Tests`.

| Unit | Responsibility |
|---|---|
| `PatternParser` | `pattern` string → syntax tree; resolves `subsets`; enforces the §1 grammar rules. |
| `ExpressionParser` | `define`/`measures` strings → typed syntax trees; enforces the §2 context rules; exposes the set of referenced columns and `SIMILARITY` terms. |
| `ProgramCompiler` | Syntax tree → instruction list (`MatchLabel`, `Split`, `Jump`, `Save`, `ExclusionStart`, `ExclusionEnd`, `MatchStart`, `MatchEnd`, `Done`). `Split` operand order encodes greedy versus reluctant preference; `PERMUTE` is expanded to alternation in lexicographic preference order. |
| `ExpressionEvaluator` | Evaluates syntax trees against a thread's match state (label assignments, capture positions, per-variable aggregations) plus the partition's rows and the similarity score map. |
| `Matcher` | Port of Trino's `io.trino.operator.window.matcher.Matcher` (Apache-2.0; attributed in the file header and in a new repository-root `THIRD-PARTY-NOTICES.md`, which does not exist yet). Threads run in priority order; equivalent threads are pruned using an equivalence check restricted to the state the `define` expressions actually read. When no `define` reads match history, this degenerates to the linear-time Thompson/Pike simulation. |
| `MatchOutputBuilder` | Applies rows-per-match, `measures` (running/final), exclusion, empty and unmatched rows, and `AFTER MATCH SKIP`. |
| `PatternBudget` | Tracks the §5 counters; throws `PatternBudgetExceededException` naming the budget. |

Public entry point: `PatternQuery.Compile(request parts) → CompiledPattern` (throws
`PatternValidationException`) and
`CompiledPattern.Run(partitionRows, similarityScores, budget) → IEnumerable<MatchOutputRow>`.

#### 3.2 Row sources

**`TYPE_ROWS` — `IEngagementStoreSearchService.MatchRowsAsync`** (`Iverson.StarRocks`), returning
`IAsyncEnumerable<IDictionary<string, object?>>`:

- Validates `partition_by`, the `order_by` properties, every `where` clause property, and every
  column referenced by `define`/`measures`/`SIMILARITY` against `ColumnsFor(schema, constraint)`
  minus `excludedColumns`. This is a membership check performed before `BuildWhere` runs, the same
  way `Pipeline` checks `base_where` with `RequireColumn`. `ColumnsFor` already excludes the tenant
  column and fields the caller is not allowed. An unknown, hidden or excluded column throws
  `EngagementQueryTranslationException`. It also receives the `measures` names and applies the §1
  output-column comparison for the requested rows-per-match shape.
- Bytes columns are excluded. `ObjectSearchGrpcService` passes, as an `excludedColumns` argument,
  the names of `schema.ScalarColumns` whose `SqlType` is `BYTEA` (ordinal-ignore-case; `BYTEA[]`
  arrays are stored as `STRING` and are unaffected). `MatchRowsAsync` removes them from the
  `ColumnsFor` set before validation and projection, so referencing one in any slot is
  `InvalidArgument` and `ALL_ROWS_*` output omits them. `EngagementQuerySchema` is unchanged.
- Reuses the `SearchClause` WHERE builder and the existing authorization-constraint row filter.
- SQL: `SELECT <key>, <columns> FROM <tenant db>.<table> WHERE <where + authz>
  ORDER BY <partition_by>, <order_by>, <key> LIMIT <MaxRowsScanned + 1>`. `<columns>` is the
  referenced columns for `ONE_ROW`, or all validated columns (`ColumnsFor` minus
  `excludedColumns`) for `ALL_ROWS_*`. Identifiers come only from `ColumnsFor`.
- Executes through a **new streaming tenant-scoped wrapper**. The existing
  `RunTenantScopedAsync` returns `Task<T>` and disposes its connection, so it cannot stream. The
  new wrapper opens the connection, runs `SET ROLE`, reads unbuffered, and holds the connection
  until enumeration ends; `SET ROLE NONE` runs on release, with the same failure-swallowing
  discipline. The resilience pipeline wraps only the open, `SET ROLE` and query-start steps
  (before the first row is yielded); a failure after that propagates.
- Tenant and missing-resource handling match `SearchAsync`: a null or invalid tenant, or
  `IsExpectedMissingResourceError`, yields an empty sequence.

**`CHUNKS` — `IChunkRowSource`** (`Iverson.Vector`), backed by a new
`IVectorQueryService.ScrollAsync(collection, filter, payloadFields, vectorName?, pageSize)`. A
Qdrant scroll cannot deliver one parent's chunks contiguously: `order_by` needs an integer, float
or datetime payload index, but `parent_id` is a keyword, `chunk_index` is stored as a string, and
chunk point IDs are hashes. The source therefore reads in two phases:

1. Scroll the tenant's chunks collection with filter = `BuildChunksFilter(where)` AND
   `field = chunkDesc.PropertyName` (the canonical spelling resolved in §3.4 step 3) AND the
   ownership filter (`ApplyOwnership`), returning only the
   `parent_id` payload and no vectors. Collect the distinct parent keys, counting chunks against
   `MaxRowsScanned`, then sort the keys ordinally.
2. For each batch of parent keys (bounded by `BatchRows` chunks, using the phase-1 per-parent
   counts), scroll with the same filter AND `parent_id ∈ batch` (`Conditions.Match(field, list)`),
   returning `text`, `parent_id`, `chunk_index` and, when the request uses `SIMILARITY`, the
   `chunkDesc.PropertyName.ToSnakeCase() + "_vector"` vector. Group by parent and sort by
   `int.Parse(chunk_index)`. If the vector-selecting scroll fails because the collection lacks the
   vector (§3.3), the same scroll is re-issued without the vector selector and every row's vector
   is absent.

A chunks collection that does not exist (Qdrant `NotFound`) yields an empty sequence, as
`SearchChunks` does. `where` with more than one clause and `OR` logic is `InvalidArgument`.
`SearchChunks` silently ANDs such a filter; this RPC rejects it instead.

`ObjectSearchGrpcService` builds the CHUNKS filter (`BuildChunksFilter` + `ApplyOwnership`), the
canonical `field` value and the vector name, and passes them to `IChunkRowSource`.
`IChunkRowSource` stays in `Iverson.Vector`; its inputs are the resolved chunks collection name,
the `Filter`, the `field` value, the optional vector name, the `BatchRows` bound and the
`MaxRowsScanned` bound. When phase 1 counts more chunks than `MaxRowsScanned`, the source throws
`PatternBudgetExceededException`; `Iverson.Vector` therefore gains a project reference to
`Iverson.Patterns`, which has no store dependencies and so introduces no cycle.

#### 3.3 `SimilarityResolver` (`Iverson.Api`)

1. Collects the distinct `(col, text)` terms from the compiled expressions.
2. Embeds each distinct text once with `resolver.Get(SchemaDescriptor.ModelOf(schema)).EmbedQueryAsync`.
3. `TYPE_ROWS`: for each batch, calls `RetrieveNamedVectorAsync(objectCollection, ids,
   <the §2 vector name>)`, where `ids` are `IntelligenceStoreConsumer.KeyToUlong(key)` for the batch's
   rows, under a read-only scoped API key. `CHUNKS`: uses the vectors returned by the phase-2
   scroll.
4. Scores each present vector with `TensorPrimitives.CosineSimilarity(queryVector, vector)`. The
   result is a map from `(row, term)` to a double; absent entries evaluate to `NULL`.

It must **not** use `RetrieveVectorsOrDegradeAsync`. That wrapper turns a failure into absent
vectors, which would silently become `NULL` and change the results. A retrieve or scroll failure
fails the request, with two exceptions, each of which means every row in the batch has an absent
vector (`NULL`):
- an `RpcException` with `StatusCode.NotFound` from the object-collection retrieve (the tenant's
  collection has not been created yet);
- an `RpcException` with `StatusCode.InvalidArgument` whose detail contains
  `Not existing vector name` (the collection predates the vector; the consumer adds it on the
  tenant's next write). For the phase-2 scroll, the scroll is re-issued without the vector
  selector (§3.2). The match is on message text, so a wrong vector name is indistinguishable from
  this state; §2's name resolution from `schema.VectorFields`/`ChunkFields` is the guard.

#### 3.4 `ObjectSearchGrpcService.MatchPattern`

1. `RequireSchema(type_name)`.
2. Validate the limits (§5), then `PatternQuery.Compile`. `PatternValidationException` maps to
   `InvalidArgument`. No store is touched before this step succeeds.
3. `CHUNKS`: resolve `chunk_property` from `schema.ChunkFields` (`InvalidArgument` if absent), and
   require a Qdrant collection (`FailedPrecondition` if absent, as `SearchChunks` does).
4. Authorization. `TYPE_ROWS` uses `EvaluateAuthorization(schema, [])`, as `Pipeline` does.
   `CHUNKS` uses `authEvaluator.Evaluate(..., Read)`, as `SearchChunks` does, and requires
   `chunkDesc.PropertyName` (the canonical spelling resolved in step 3) to be in `AllowedFields`.
   A denied caller gets an **empty stream**. For
   `TYPE_ROWS`, each `SIMILARITY` column is first resolved to its `VectorFields` descriptor (§2),
   and `descriptor.PropertyName` must be in `AllowedFields` when that set is non-null
   (`InvalidArgument` otherwise). For `CHUNKS`, `SIMILARITY(text, …)` needs no further check: the
   chunk vector belongs to `chunk_property`, whose `chunkDesc.PropertyName` is already required to
   be in `AllowedFields`.
5. Log `[MatchPattern] type=… source=… defines=… measures=…` with sanitized values, as the other
   RPCs do.
6. Stream rows from the source into batches of whole partitions (§4). Resolve similarity for each
   batch, run the matcher per partition, and write each output row: `AuthorizationFieldMasking.RemoveTenantColumn`,
   then `DictToProtoStruct`, then `WriteAsync`.
7. Stop once `limit` output rows have been written. Stopping ends the StarRocks read or Qdrant
   scroll.

#### 3.5 Clients

Following the existing `PipelineBuilder` + `EntityCoordinator.PipelineAsync` convention, each of
the five SDKs gets:

- a small `MatchPatternBuilder` with setters for every request field (the pattern and expression
  strings pass through unchanged, with no per-language expression builder);
- a `MatchPatternAsync` (language-idiomatic name) coordinator method that streams dictionaries
  from `MatchPatternResponse.data`, together with `match_number` and `classifier`.

Proto regeneration uses each SDK's existing script or build step.

### 4. Data flow and memory

`source rows → partition batches (whole partitions, target BatchRows = 2,000 rows) → similarity
resolution for the batch → per-partition matching → output`. A partition is never split. When a
single partition exceeds `BatchRows` it forms a batch on its own, up to `MaxPartitionRows`. Memory
is bounded by one batch, plus the matcher state of one partition, plus the similarity map for one
batch.

### 5. Limits

A new `PatternQueryLimitOptions` class with `Section = "Patterns:Limits"`, constructed in
`Program.cs` from individual `cfg.GetValue` reads in the same way as `EngagementQueryLimitOptions`.

| Limit | Default | When exceeded |
|---|---|---|
| `MaxPatternLength` | 1,000 characters | `InvalidArgument` |
| `MaxExpressionLength` | 1,000 characters (each) | `InvalidArgument` |
| `MaxDefines` / `MaxMeasures` / `MaxSubsets` | 50 each | `InvalidArgument` |
| `MaxSimilarityTerms` (distinct `(col, text)`) | 10 | `InvalidArgument` |
| `where` clause count | `StarRocks:QueryLimits:MaxClauses` | `InvalidArgument` |
| `limit` | 0 → 1,000; maximum `MaxOutputRows` = 10,000 | `InvalidArgument` above the maximum |
| `MaxRowsScanned` (per request) | 100,000 | `ResourceExhausted` |
| `MaxPartitionRows` | 10,000 | `ResourceExhausted` |
| `MaxActiveThreads` (per partition) | 10,000 | `ResourceExhausted` |
| `MaxSteps` (matcher steps per request) | 10,000,000 | `ResourceExhausted` |
| `TimeoutSeconds` | 30, or the client deadline if earlier | `DeadlineExceeded` |

A limit hit after output rows have already been written ends the stream with that error status.
The stream never ends with success after a limit was hit. The only stop that ends with success is
reaching `limit`.

### 6. Failure semantics

| Condition | Result |
|---|---|
| Unknown type | As `RequireSchema` does today |
| Pattern or expression validation, unknown or hidden column, bad `chunk_property`/`SIMILARITY` column, `EngagementQueryTranslationException`, `FilterTranslationException`, `EmptyEmbeddingInputException`, run-time evaluation error | `InvalidArgument` |
| Caller denied read on `type_name` | Empty stream; no store queried |
| No Qdrant collection for a `CHUNKS` or `SIMILARITY` type | `FailedPrecondition` |
| Tenant has no chunks collection yet, or the StarRocks table is missing | Empty stream |
| `EngagementNotReadyException` | `Unavailable` |
| `EngagementStoreDisabledException` | `FailedPrecondition` |
| Embedding failure | `Unavailable` ("Embedding service unavailable."), as `SearchChunks` does |
| `PatternBudgetExceededException` | `ResourceExhausted` |
| Timeout | `DeadlineExceeded` |
| Any other StarRocks or Qdrant exception | Propagates exactly as it does from `Pipeline` and `SearchChunks` today |
| A row with no vector for a `SIMILARITY` term | `NULL` (not an error) |
| Tenant's object collection not created yet (Qdrant `NotFound`), or the collection lacks the vector (Qdrant `InvalidArgument` "Not existing vector name") | `NULL` for that term's rows (§3.3) |

### 7. Authorization and tenancy

- Row authorization: the StarRocks authz row filter (`TYPE_ROWS`); the ownership filter and
  scoped API key (`CHUNKS` and `SIMILARITY` retrieval).
- Field authorization: `ColumnsFor` makes hidden fields unreferenceable (`TYPE_ROWS`); the
  `AllowedFields` checks from §3.4 step 4.
- Tenant: the tenant StarRocks database and role (`SET ROLE`); the tenant Qdrant collections via
  `tenantScope.ResolveCollectionName`.
- Tenant column: excluded by `ColumnsFor` (unreferenceable) and stripped from every output row by
  `RemoveTenantColumn`.
- Rate limiting and acting-user propagation: the global `RateLimitInterceptor` and
  `ActingUserInterceptor` apply unchanged.

### 8. Consistency

StarRocks and Qdrant are populated independently from Kafka. A row visible in StarRocks whose
vector, collection or vector configuration is not yet in Qdrant yields `NULL` for `SIMILARITY`, so predicates over it are not
satisfied. This is accepted, deterministic behaviour and is not flagged in the response.

### 9. Testing

1. **`Iverson.Patterns.Tests` (no containers).**
   - Parser and compiler golden programs for every §1 construct, including the `PERMUTE` expansion
     order, reluctant quantifiers, exclusion, anchors and the empty pattern.
   - Every §1/§2 validation rule, including distinct `define` variables, distinct `subsets`
     names, and `measures` names (distinct; not the tenant column in any case; for `CHUNKS`, not
     an output column of the chosen shape — `parent_key` in `ONE_ROW`, all three in
     `ALL_ROWS_*`), plus the `FINAL`-in-`define` rejection. The `TYPE_ROWS` output-column
     comparison is tested in §9.3, where its operand exists.
   - Evaluator: three-valued logic, the §2 `NaN` semantics (one test per construct, including a
     `NULL` other operand), `MATCH_NUMBER()` and an explicit `RUNNING` prefix inside `define` as
     well as `measures`, every navigation form including out-of-range, running versus
     final, aggregates over subsets, `TIMESTAMPDIFF`, and integer division.
   - Matcher: preference order; thread pruning (asserted through step counts on a pattern that is
     exponential without pruning); every `AFTER MATCH SKIP` mode, including the illegal skips;
     every rows-per-match mode.
   - Each budget trips with the correct exception.
2. **Differential oracle.** A Trino Testcontainer (`trinodb/trino`, memory connector) is started
   once through an `ICollectionFixture`.
   - (a) Port the cases from Trino's `TestRowPatternMatching` (inline `VALUES` data).
   - (b) A seeded generator of small random patterns, `define` predicates and data.
   - Both load identical rows (the key column is included as the ordering tie-breaker) and require
     identical ordered output, including `MATCH_NUMBER()` and `CLASSIFIER()`. The generator emits
     only constructs with identical semantics in both engines, so it excludes `SIMILARITY` and
     `TIMESTAMPDIFF`, which unit tests cover.
3. **Store integration** (existing StarRocks and Qdrant container fixtures).
   - `MatchRowsAsync`: ordering including the key tie-breaker; column projection per rows-per-match
     mode; authz row filter; hidden-field, tenant-column and bytes-column rejection in every slot,
     including `where`; bytes columns omitted from `ALL_ROWS_*`; a measure colliding with a
     projected or partition column is rejected, in both rows-per-match shapes; overflow at
     `MaxRowsScanned + 1`; reading more rows than one batch without buffering.
   - `IChunkRowSource`: both phases; `chunk_index` sorted numerically (`10` after `2`); a missing
     collection returns empty; the ownership filter is applied; OR-filter rejection; a collection
     lacking the vector re-issues the phase-2 scroll without it; phase 1 over `MaxRowsScanned`
     chunks raises the budget exception.
   - `SimilarityResolver`: scores for every present vector; an absent point gives `NULL`; a
     missing object collection and an unconfigured vector give `NULL`; any other retrieve failure
     propagates (it is not treated as `NULL`).
4. **gRPC service tests:** every §6 row; a budget hit after output was written ends the stream
   with an error; a denied caller gets an empty stream; a re-cased `chunk_property` is accepted
   for a field-restricted caller that is allowed the property; tenant isolation.
5. **Client conformance:** one scalar-pattern case and one `SIMILARITY` case, across all five SDKs.
6. **Manual mutation checks** in every task review (no mutation tool is installed): delete or
   invert the named production branch, confirm the covering test fails, restore, and confirm
   `git status --porcelain` is clean.

## Out of scope

- A Postgres row source; SQL pushdown of simple patterns; the window-clause form of row pattern
  recognition (`SEEK`/`INITIAL`).
- `SIMILARITY` on a related type's vector (through an FK), or on anything other than the row's own
  type (or, for `CHUNKS`, the chunk's own vector).
- Using a pattern result as a ranking signal inside `SearchSimilar`/`SearchChunks`.
- Changing `SearchChunks`' existing silent AND of an OR filter.

## Known issues / accepted

- Ingest lag between StarRocks and Qdrant produces `NULL` similarity (§8).
- History-dependent `define` predicates can make matching exponential in the worst case. It is
  bounded by `MaxActiveThreads`, `MaxSteps` and the timeout, not prevented.
- The `CHUNKS` source scans the filtered chunk set twice (the phase-1 parent list, then the batched
  phase-2 reads) to keep memory bounded.
- `NaN` from a zero-magnitude vector is kept (§2): `NOT`/`<>` predicates over it with a non-`NULL`
  operand qualify the row, and how each SDK decodes a `NaN` `number_value` is unverified.
- The unconfigured-vector case (§3.3) is detected by Qdrant message text, which may change across
  Qdrant versions, and it would also mask a wrong-vector-name bug.

## Verified assumptions

| Assumption | Evidence |
|---|---|
| One proto feeds the server and all five SDKs | `Iverson.Client.Contracts.csproj:17`, `Java/client/pom.xml:104`, and the `generate_protos.sh` scripts for Python, TS and Go all read `Common/Proto` |
| SDK convention is a builder plus a coordinator method | `DotNet/.../EntityCoordinator.cs:296` `PipelineAsync(PipelineBuilder)`; `PipelineBuilder` in DotNet, Java and Go; `core.py`, `core.ts` |
| Pipeline-style authorization, denied → empty stream, and its exception mapping | `ObjectSearchGrpcService.cs:860-927` |
| Tenant column and hidden fields are excluded from referenceable columns | `StarRocksPipelineBuilder.cs:39-57` (`ColumnsFor`) |
| StarRocks tenant wrapper cannot stream | `EngagementRepository.cs:158-190` (`RunTenantScopedAsync` returns `Task<T>`; `await using var conn`); callers use buffered `conn.QueryAsync` (`:361`) |
| Chunk filter, ownership, `AllowedFields` and scoped API key | `ObjectSearchGrpcService.cs:495-545`, `:634-650` |
| Chunk payload is `parent_id`/`field`/`chunk_index` (string); no index on `chunk_index` | `IntelligenceStoreConsumer.cs:292-295`; no `CreatePayloadIndex` for it |
| Qdrant `order_by` requires a range-capable (integer/float/datetime) index | Qdrant indexing docs and issue #1763 (web search 2026-09-17) |
| Match-any on keywords and `HasId` exist | `Qdrant.Client` 1.18.1 XML: `Conditions.Match(string, IReadOnlyList<string>)`, `Conditions.HasId(IReadOnlyList<ulong>)` |
| Key → point ID; vector naming is `PropertyName.ToSnakeCase() + "_vector"`, with the property resolved case-insensitively from `VectorFields`/`ChunkFields`; chunk `field` holds the canonical property name | `IntelligenceStoreConsumer.cs:650` `KeyToUlong`; `ObjectSearchGrpcService.cs:162-163,253,506-507,638`; CDR-1 probes P2/P7 (Qdrant stores `title_vector`; the unconverted name fails) |
| Project references: `Iverson.Vector` → Contracts today, and `Iverson.Patterns` under this design (Contracts itself references no project, so no cycle); `Iverson.Api` → Embeddings, Sql, StarRocks, Vector, Events, Contracts; `ModelOf`, `KeyToUlong`, `ToSnakeCase` are `internal` to Api and `BuildChunksFilter` is `private` | `Iverson.Vector.csproj:26`; `Iverson.Client.Contracts.csproj` (no `ProjectReference`); `Iverson.Api.csproj:42-47`; `SchemaDescriptor.cs:27`; `IntelligenceStoreConsumer.cs:650`; `NamingExtensions.cs:5`; `ObjectSearchGrpcService.cs:1130` |
| `ColumnsFor` is `private` to `Iverson.StarRocks` and its values are canonical column spellings; the authorization constraint it needs is built at §3.4 step 4; `EngagementQueryTranslationException` is public | `StarRocksPipelineBuilder.cs:39-58`; `ObjectSearchGrpcService.cs:1070-1080`; `EngagementQueryTranslationException.cs:9`; CDR-2 probe P3 (StarRocks returns the stored case) |
| `AllowedFields` holds canonical property names in an ordinal `HashSet` (only the excluded set is case-insensitive), and includes every `ChunkFields.PropertyName` | `RowFieldAuthorizationEvaluator.cs:80-116`; CDR-2 probe P26; CDR-1 probes P5, P8b |
| `System.Numerics.Tensors` is compile-visible to `Iverson.Api` after the §3.3 relocation: a transitive `PackageReference` from `Iverson.Vector`, with no `PrivateAssets` | `Iverson.Vector.csproj:22,26`; `Iverson.Api.csproj:45`; CDR-2 probe P25 |
| The `Not existing vector name` message covers every collection shape the codebase can hold, including legacy unnamed-vector configs, and the re-issued no-vector scroll succeeds | CDR-2 probe P23 (Qdrant 1.18.2, real `IntelligenceCollectionManager`/`IntelligenceVectorService`) |
| Every `[IversonEmbedding]` property is also a `ColumnsFor` column, so a `SIMILARITY` column passes the §3.2 membership check | `SchemaBuilder.cs:60-64` (every non-key property becomes a `ScalarColumn`, before the `IsEmbedding` branch); the only removals are the tenant column, disallowed fields and bytes columns, each already a stated rejection |
| `BuildWhere` silently drops an unresolvable property; `Pipeline` pre-validates with `RequireColumn` | `StarRocksQueryBuilder.cs:636-637`; `StarRocksPipelineBuilder.cs:100-101,408-413`; CDR-1 probe P1 |
| The field-authorization set holds canonical property names, compared case-sensitively | `ObjectSearchGrpcService.cs:173,517`; CDR-1 probes P5, P8b |
| Qdrant 1.18.2 error shapes: missing collection → `NotFound`; missing named vector (retrieve or vector-selecting scroll) → `InvalidArgument "Wrong input: Not existing vector name error: <name>"`; `RetrieveNamedVectorAsync` catches neither; collections are created/migrated only by the consumer on write | `ObjectSearchGrpcService.cs:281-287` (`NotFound` precedent); `IntelligenceVectorService.cs:156-187`; CDR-1 probes P2, P7 |
| Unbuffered read works against StarRocks 4.1.1 through a raw `MySqlDataReader` (early stop leaves the connection reusable); Dapper `QueryUnbufferedAsync` throws on reader disposal | CDR-1 probe P4 |
| `.NET` `double` `NaN` semantics as stated in §2 | CDR-1 probes P6, P13, P18 |
| Output rows are name-keyed `Struct`s: colliding names overwrite or vanish; the tenant-name strip is case-insensitive; names differing only by case survive at the server and in every SDK's untyped map | `SchemaDescriptor.cs:20-21`; `AuthorizationFieldMasking.cs:213-214`; CDR-1 probes P21, P21b, P21c |
| Bytes columns are identifiable from the schema: scalar `ClrBytes` → `SqlType` `BYTEA` (StarRocks `VARBINARY`); `BYTEA[]` → `STRING`; `EngagementQuerySchema` carries no column types | `SchemaBuilder.cs:60-64,398,419`; `SchemaDescriptor.cs:117` (`ColumnDescriptor(Name, SqlType, IsNullable)`); `EngagementQuerySchema.cs:39-54` |
| Bytes columns break output (`"System.Byte[]"`), default-equality partitioning, expression comparison, and `where` filtering | CDR-1 probes P8e, P13, P14, P16c |
| Trino 483 accepts `MATCH_NUMBER()` and a `RUNNING` prefix inside `DEFINE`, and rejects `FINAL` there ("FINAL semantics is not supported in DEFINE clause") | CDR-3 probe P27 D/E/F, re-run through `/v1/statement` when this fix was applied |
| No ported Trino case exercises `MATCH_NUMBER()` or `RUNNING` inside `DEFINE` | CDR-3 probe P28 (141 `assertions.query(` sites in `TestRowPatternMatching.java`; zero hits) |
| The new proto enum values and message names do not collide inside `package iverson` | CDR-3 §0 S3 greps (both exit 1) |
| `Qdrant.Client` 1.18.1 exposes a scroll taking filter, page size, cursor, payload selector and vector selector | CDR-3 probe P29 (`Qdrant.Client.xml` member signature) |
| The new `Iverson.Vector` → `Iverson.Patterns` edge is acyclic | CDR-3 probe P29: `Iverson.Client.Contracts.csproj` declares no `ProjectReference`, and §3.1 gives `Iverson.Patterns` no store dependencies |
| `MatchRowsAsync` can hold the measure-name comparison's second operand: `ColumnsFor` is in the same assembly and the constraint arrives as a method argument, as it does for `PipelineAsync` | `StarRocksPipelineBuilder.cs:39-55`; `IEngagementStoreRoles.cs:51-55` |
| Retrieve-by-ID and local cosine exist; the collection metric is Cosine | `IVectorRoles.cs:17`, `IntelligenceVectorService.cs:156-187`; `ResultReranker.cs:42` `TensorPrimitives.CosineSimilarity`; `IntelligenceCollectionManager.cs:17` |
| `SearchNamedAsync` exposes no exact/threshold parameter | `IntelligenceVectorService.cs:124-148` |
| Embedding call and its failure mapping | `ObjectSearchGrpcService.cs:549-567` |
| Global interceptors, no per-RPC name lists | `Program.cs:92-95`; no server references to RPC method names |
| Limits configuration pattern | `Program.cs:261-280` |
| `DateTime` converts to `Struct` | `ObjectSearchGrpcService.cs:1261` |
| Testcontainers available; Trino image ships a memory catalog | `Iverson.Api.Tests.csproj:24` (Testcontainers 4.15.0); `trinodb/trino` `core/docker/default/etc/catalog/memory.properties` |
| Trino matcher design, license and tests | Upstream `Matcher.java` (`threadsAtInstructions`, `ThreadEquivalence`); `LICENSE` Apache-2.0; `TestRowPatternMatching.java` (160 `VALUES`) |
| Iverson is MIT | Repository `LICENSE` |
| No mutation-testing tool installed | No Stryker config or tool; `~/.dotnet/tools` has only `ilspycmd` |
| Both solution files list every project | `Iverson.slnx`, `Iverson.Server/Iverson.Server.slnx` |
