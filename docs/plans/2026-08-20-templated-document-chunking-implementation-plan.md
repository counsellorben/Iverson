# Templated Document Chunking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-08-20-templated-document-chunking-design.md` (commit SHA: `cefad1cd5dc2c79ad73de67e92a12b57d675afa1`)

**Goal:** Let a type declare a document template whose rendered text — drawn from the entity's own properties and its one-hop relations — is chunked and embedded into Qdrant, and re-rendered through a throttled queue when a related entity changes.

**Architecture:** The template arrives on `TypeDescriptor`, is parsed and validated once at schema registration, and is stored on `SchemaDescriptor`. `SchemaBuilder` appends a synthetic `ChunkDescriptor` named `Document` so the existing chunk pipeline produces `document_vector` / `document_centroid` with no new vector code. At ingest, `IntelligenceStoreConsumer` renders the template instead of reading a column. A dedicated consumer detects related-entity changes and enqueues re-renders into a durable table drained by a throttled worker.

**Tech stack:** .NET 10 (`net10.0`), xunit 2.9.3 + NSubstitute 5.3.0, Npgsql/Dapper, Qdrant.Client, Kafka. Server only — no client declaration surface.

---

## Global Constraints

Copied from the spec; every task must hold to these.

- **New `SchemaDescriptor` members must be nullable or defaulted, never `required`.** `SchemaRegistry.LoadAsync` deserializes pre-change `_iverson_schema` rows, and a required member missing from legacy JSON throws at startup.
- **Every FK reference is `RelationDescriptor.ForeignKey`, never a `{TypeName}Id` string built from the convention.**
- **All relation fetches on the render path are tenant-scoped**, using the authoritative tenant value derived from the Postgres row, not the event payload.
- **Rendering is culture-invariant and fixed per type.** No format specifiers, no configuration. The same row must render byte-identically on any node at any time.
- **Tables needing composite or partial uniqueness use raw DDL in their repository**, never `ApplySchemaAsync` — it cannot express constraints or indexes.
- Tests are mutation-tested, not merely green.

## File Structure

**Create**
- `Iverson.Server/Iverson.Api/Schema/DocumentTemplate.cs` — parsed template model: literal segments and typed placeholders.
- `Iverson.Server/Iverson.Api/Schema/DocumentTemplateParser.cs` — string → `DocumentTemplate`; owns structural rejections.
- `Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs` — renders a parsed template against a payload plus one-hop relations.
- `Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs` — reverse-lookup consumer that enqueues re-renders.
- `Iverson.Server/Iverson.Api/Reconciliation/DocumentRerenderQueueWorker.cs` — throttled drain + type-level expansion.
- `Iverson.Server/Iverson.Api/Reconciliation/DocumentRerenderOptions.cs` — poll interval, batch size, page size.
- `Iverson.Server/Iverson.Sql/DocumentRerenderQueueRepository.cs` — raw-DDL bootstrap, insert, poll, delete, count, page.
- `Iverson.Server/Iverson.Sql/DocumentRerenderQueueRow.cs` — row record.

**Modify**
- `Iverson.Clients/Common/Proto/object_mapping.proto` — four type-level fields.
- `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs` — `DocumentTemplate` member.
- `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs` — synthetic `Document` chunk field.
- `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — validation second pass; backfill enqueue.
- `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs` — render hook; orphan delete.
- `Iverson.Server/Iverson.Vector/IntelligenceFilterBuilder.cs` — parent+field predicate.
- `Iverson.Server/Iverson.Events/EntityEvent.cs` — `PriorPayloadJson`, `SuppressRerenderCascade`.
- `Iverson.Server/Iverson.Api/Grpc/OutboxPublisher.cs` — thread prior payload.
- `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `ObjectPersistenceGrpcService.cs` — pass the already-fetched prior row.
- `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs` — reverse-dependency index.
- `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs` — queue repository interface; two `IEntityRepository` methods.
- `Iverson.Server/Iverson.Sql/EntityRepository.cs` — array containment; paged key+tenant read.
- `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs` — queue-depth gauge.
- `Iverson.Server/Iverson.Api/Program.cs` — DI, table bootstrap, hosted services.

**Test**
- `Iverson.Api.Tests/Schema/DocumentTemplateParserTests.cs`, `DocumentTemplateValidationTests.cs`
- `Iverson.Api.Tests/Consumers/DocumentRendererTests.cs`, `DocumentRerenderConsumerTests.cs`
- `Iverson.Api.Tests/Reconciliation/DocumentRerenderQueueWorkerTests.cs`, `DocumentRerenderQueuePostgresIntegrationTests.cs`
- Additions to `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`, `Iverson.Api.Tests/Schema/SchemaBuilderTests.cs`

## Inherited from spec

Verified by `thorough-brainstorming` at spec-write time; **not** re-verified here. Trusted as ground truth — see the spec's "Verified assumptions" section for the full list and evidence. Load-bearing items this plan builds directly on:

- `ChunkDescriptor(PropertyName, MaxTokens, Overlap, ModelId, Dimension, Contextual)` and `SchemaDescriptor.ChunkFields` admit a synthetic entry — `SchemaDescriptor.cs:57`.
- `ToChunkCollectionSchema` / `ToCollectionSchema` derive `_vector` and `_centroid` purely from `ChunkFields` — `SchemaBuilder.cs:213`, `SchemaBuilder.cs:220`.
- `SearchChunks` routes by property name against `ChunkFields` — `ObjectSearchGrpcService.cs:301`; authorizes via `AllowedFields.Contains(...)` — `ObjectSearchGrpcService.cs:312`.
- Chunk model id and dimension come from `IEmbeddingService` — `SchemaBuilder.cs:66`.
- The chunk loop `continue`s on empty text, so a synthetic field needs an explicit hook — `IntelligenceStoreConsumer.cs:183`.
- Template validation must run after the `RootType.Concat(Dependents)` loop — `SchemaRegistrationOrchestrator.cs:33`.
- Chunk points are deleted by filter only in `HandleDeleteAsync` — `IntelligenceStoreConsumer.cs:488`.
- Neither relation fetch specifies an order — `EntityRepository.cs:18-27`.
- `ApplySchemaAsync` cannot create constraints or indexes — `IRecordStoreRoles.cs:123-129`, `PostgresSchemaManager.cs:44-52`, `PostgresSchemaManager.cs:113-124`.
- Relation foreign keys are overridable — `ManyToOneAttribute.cs:10`, `OneToManyAttribute.cs:11`, `object_mapping.proto:73`.
- Both update paths already fetch the prior row — `ObjectMappingGrpcService.cs:335`, `ObjectPersistenceGrpcService.cs:102`.
- The consumer and worker are gated on `workloadRole == "worker"` — `Program.cs:250`.

## Verified plan-level assumptions

Newly introduced by this plan and verified at plan-write time.

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | Repository *implementations* live in `Iverson.Sql/`, not `Iverson.Api/Reconciliation/` | `Iverson.Sql/ReconciliationQueueRepository.cs`, `DlqRepository.cs`, `EnrichmentStateRepository.cs` all in `Iverson.Sql/`; `Iverson.Api/Reconciliation/` holds only schema definitions and workers |
| 2 | File path | Test subdirectories `Schema/`, `Consumers/`, `Reconciliation/` exist | `ls Iverson.Api.Tests/` — all three present |
| 3 | File path | `object_mapping.proto` has one source copy | `find -name object_mapping.proto` → `Iverson.Clients/Common/Proto/` only (Java `target/classes` copy is a build artifact) |
| 4 | Signature | `IRecordStoreQueryExecutor` exposes `QueryAsync<T>` / `ExecuteAsync` / `QuerySingleOrDefaultAsync`, each `(sql, param, tenantScoped, tenantId)` | `IRecordStoreRoles.cs:5-7` |
| 5 | Signature | `IEventProducer.ProduceAsync<T>(string topic, string key, T message) where T : class` | `IEventProducer.cs:5` |
| 6 | Signature | `IEventConsumer.ConsumeAsync(topic, groupId, Func<string,string,CancellationToken,Task>, ct)` | `IEventConsumer.cs:7-11` |
| 7 | Signature | `ConsumerResilience.RunWithRestartAsync(Func<Task>, ILogger, string, CancellationToken, TimeSpan?)` | `ConsumerResilience.cs:12-17` |
| 8 | Signature | `IVectorWriteService.DeleteByFilterAsync(string collectionName, Filter filter)` | `IVectorRoles.cs:46` |
| 9 | Signature | `IOutboxPublisher.PublishAsync` takes 9 params ending `string opLabel, CancellationToken ct = default` | `OutboxPublisher.cs:9-18` |
| 10 | Signature | `SchemaRegistry` exposes `LoadAsync(CancellationToken)` and `RegisterAsync(SchemaDescriptor)` — the two reverse-index rebuild points | `SchemaRegistry.cs:20`, `SchemaRegistry.cs:47` |
| 11 | Signature | `allFields` in the authorization evaluator already concatenates `ChunkFields` property names | `RowFieldAuthorizationEvaluator.cs:73-77` — so "`Document` always in `AllowedFields`" needs **no code**; T2 implements only the rejection rule |
| 12 | Signature | `BuildDescriptor` assembles a local `chunks` list assigned to `ChunkFields`, and sets `CollectionName` non-null when `chunks.Count > 0` | `SchemaBuilder.cs:160-170` — a template-only type therefore gets a collection, as the design requires |
| 13 | Command | Test: `dotnet test Iverson.Server/Iverson.Server.slnx` — SDK 10.0.111 supports `.slnx`; xunit 2.9.3 + NSubstitute 5.3.0 | `dotnet --version` → `10.0.111`; `Iverson.Server.slnx` lists all projects; `Iverson.Api.Tests.csproj:10-16` |
| 14 | Command | Build: `dotnet build Iverson.Server/Iverson.Server.slnx`; target framework `net10.0`, nullable enabled | `Iverson.Api.csproj:4-5` |
| 15 | Command | Commit style is lowercase imperative, no mandatory Conventional-Commits prefix | `git log --oneline -15` — mixture of bare imperative and occasional `docs:`/`fix:`/language prefixes; bare imperative is safe |
| 16 | Ordering | T1 leaves no broken intermediate state: with `document_template` empty, no synthetic chunk field is appended, so behavior is unchanged until a template exists — and no client emits one | `SchemaBuilder.cs:160-170` (synthetic entry is conditional on the template); spec Scope — feature is dormant until client work lands |
| 17 | Ordering | T4 consumes `DocumentRenderer` (T3); T7 and T9 consume the queue repository (T6); T8 consumes T5's `SuppressRerenderCascade` and T6's repository | Task Interfaces sections below |
| 18 | Ordering | T2 and T3 depend only on T1; neither imports anything T4-T9 introduce | Task Interfaces sections below |
| 19 | Code validity | Passing a `Guid[]` (not `string[]`) for a uuid-array parameter is the established pattern | `EntityRepository.cs:15-20` — explicit comment: "Guid[], not string[]: Npgsql sends string[] as text[], which blocks Postgres from using the uuid primary key index" |
| 20 | Code validity | `Conditions.MatchKeyword(field, value)` is the filter primitive already used for chunk-point predicates | `IntelligenceFilterBuilder.cs:57-60` (`MatchParentId`) |
| 21 | Code validity | Chunk points carry `["field"] = cf.PropertyName`, so a parent+field delete is expressible | `IntelligenceStoreConsumer.cs:254` |
| 22 | Code validity | Adding a gauge to `ReconciliationTelemetry` needs no OTel wiring change — the meter is already registered by name | `Program.cs:68` — `.AddMeter("Iverson.Events", ReconciliationTelemetry.MeterName)` |
| 23 | Consumer impact | `EntityEvent` has exactly 4 construction sites outside tests, all positional; two new trailing optional params compile at all of them | `OutboxPublisher.cs:45`, `ReconciliationService.cs:45`, `:116`, `:158` |
| 24 | Consumer impact | `EntityRepository` is the only implementer of `IEntityRepository`; adding two methods breaks no hand-written fake | `grep ": IEntityRepository"` → one hit, `EntityRepository.cs:5`; tests use NSubstitute |
| 25 | Consumer impact | `ExtractString(JsonElement, string)` is already duplicated as a private static in two consumers; `DocumentRenderer` adds a third private copy rather than refactoring two files the spec did not authorize | `EnrichmentConsumer.cs:330`, `IntelligenceStoreConsumer.cs:655` |
| 26 | Sibling sweep | Every type/method named across all nine tasks resolves at its point of use | Swept `ChunkDescriptor`, `SchemaDescriptor`, `SchemaBuilder`, `TypeDescriptor`, `RelationDescriptor`, `RelationKind`, `SchemaRegistry`, `IEntityRepository`, `IRecordStoreQueryExecutor`, `IEventProducer`, `IEventConsumer`, `ConsumerResilience`, `IntelligenceFilterBuilder`, `IVectorWriteService`, `IntelligenceTenantScope`, `IEmbeddingService`, `ReconciliationTelemetry`, `StoreTargeting`, `EntityEvent`, `EntityTopics`, `IOutboxPublisher`, `TableSchema`, `ColumnSchema` — all resolve to existing definitions |
| 27 | Sibling sweep | Every new repository method matches the executor signature shape `(sql, param, tenantScoped, tenantId)` | `IRecordStoreRoles.cs:5-7`; existing methods in `EntityRepository.cs:7-30` all follow it |

---

## Tasks

### Task 1: Wire contract, template model, and parser

**Files:**
- Create: `Iverson.Server/Iverson.Api/Schema/DocumentTemplate.cs`
- Create: `Iverson.Server/Iverson.Api/Schema/DocumentTemplateParser.cs`
- Modify: `Iverson.Clients/Common/Proto/object_mapping.proto`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaDescriptor.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs`
- Test: `Iverson.Api.Tests/Schema/DocumentTemplateParserTests.cs`, additions to `SchemaBuilderTests.cs`

**Interfaces:**
- Produces: `DocumentTemplate` model, `DocumentTemplateParser.Parse`, `SchemaDescriptor.DocumentTemplate`, the synthetic `Document` entry in `ChunkFields`.

- [ ] **Step 1: Add the four type-level proto fields**

```proto
// in message TypeDescriptor, after `string description = 6;`
string document_template   = 7;   // [IversonDocument] template; empty = none
int32  document_max_tokens = 8;
int32  document_overlap    = 9;
bool   document_contextual = 10;
```

- [ ] **Step 2: Define the parsed model**

`DocumentTemplate` holds an ordered list of segments. Segment kinds: literal text, scalar placeholder (`{Prop}`), one-hop placeholder (`{Rel.Prop}`), and block (`{#Rel}` … `{/Rel}`) carrying its own inner segment list. Blocks cannot nest, so the inner list admits only literal and scalar segments. Records, not classes — this is serialized onto `SchemaDescriptor`.

- [ ] **Step 3: Write parser tests first**

Cover: literal-only rejection (zero placeholders), `{{` escape, each placeholder form, block with inner scalars, unclosed block, nested block, mismatched close tag (`{#Tags}…{/Authors}`), unparseable placeholder, two-hop (`{A.B.C}`), and a dotted placeholder inside a block.

- [ ] **Step 4: Implement the parser**

Structural rejections only — the parser knows nothing about schemas. Semantic validation is Task 2. Throw a dedicated exception type carrying the offending placeholder text so Task 2 can surface it in an `RpcException` message.

- [ ] **Step 5: Add `DocumentTemplate` to `SchemaDescriptor`**

Nullable, not `required` — legacy `_iverson_schema` JSON predates it (Global Constraints). Confirm it round-trips through `System.Text.Json` with the options `SchemaRegistry` uses.

- [ ] **Step 6: Append the synthetic chunk field in `SchemaBuilder.BuildDescriptor`**

When `typeDesc.DocumentTemplate` is non-empty, parse it, store the result, and append to the local `chunks` list before it is assigned at `SchemaBuilder.cs:167`:

```csharp
chunks.Add(new ChunkDescriptor(
    "Document",
    typeDesc.DocumentMaxTokens,
    typeDesc.DocumentOverlap,
    embedding.ModelId,
    embedding.Dimension,
    typeDesc.DocumentContextual));
```

Do **not** add `"Document"` to `largeFields` — it is not a column.

- [ ] **Step 7: Test the builder**

A descriptor built from a `TypeDescriptor` with a template has a `Document` chunk field with the model/dimension from `IEmbeddingService` and a non-null `CollectionName` even when the type declares no other vector or chunk field. A descriptor built without a template is byte-identical to today's.

- [ ] **Step 8: Run tests and commit**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Clients/Common/Proto/object_mapping.proto Iverson.Server/Iverson.Api/Schema/ Iverson.Server/Iverson.Api.Tests/Schema/
git commit -m "add document template model, parser, and synthetic chunk field"
```

---

### Task 2: Registration validation

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: `Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`

**Interfaces:**
- Consumes: `DocumentTemplate` and the parser (T1).

- [ ] **Step 1: Write the rejection tests first**

One test per rule, all asserting `RpcException` with `InvalidArgument` (or `FailedPrecondition` for the dependent-breaking case): undeclared property; undeclared relation; scalar not declared on the target type; `{Rel.Prop}` on a collection relation; `{#Rel}` on a single-valued relation; derived-vector-name collision **including a lowercase `document` property**; a template referencing a `FieldPermission`-carrying property on the declaring type; the same on a one-hop target; a `FieldPermission` naming `Document`; and re-registering a target type without a property a dependent's template references.

Plus two positive tests: a root template referencing a type that appears later in `dependents` validates successfully; and — guarding the regression the companion rule exists to prevent — `SearchChunks(property: "document")` succeeds on a type declaring an unrelated `FieldPermission`.

- [ ] **Step 2: Move template validation into a second pass**

`RegisterAsync` iterates `RootType.Concat(Dependents)` (`SchemaRegistrationOrchestrator.cs:33`). Collect the registered descriptors during the loop, then run `ValidateDocumentTemplate` over all of them after the loop closes, so a root's reference to a dependent resolves.

- [ ] **Step 3: Implement `ValidateDocumentTemplate`**

Sits beside `ValidateFieldReference` and `ValidateEnrichmentTargets`. Resolves each placeholder against the declaring descriptor and, for one-hop forms, the target descriptor found via `relation.RelatedTypeName`. Relation lookup is by `PropertyName`; the FK is never derived from a name convention (Global Constraints).

Collision rule is stated over derived names, not spellings:

```csharp
// "Document", "document", and "DOCUMENT" all derive "document_vector".
var duplicate = descriptor.ChunkFields
    .GroupBy(c => c.PropertyName.ToSnakeCase(), StringComparer.Ordinal)
    .FirstOrDefault(g => g.Count() > 1);
```

- [ ] **Step 4: Reject a `FieldPermission` naming `Document`**

In the same validator. Note the companion rule needs no code: `allFields` already concatenates `ChunkFields` property names (`RowFieldAuthorizationEvaluator.cs:73-77`), so `Document` is in `AllowedFields` by construction once it can never be excluded.

- [ ] **Step 5: Run tests and commit**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs
git commit -m "validate document templates at schema registration"
```

---

### Task 3: `DocumentRenderer`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs`
- Test: `Iverson.Api.Tests/Consumers/DocumentRendererTests.cs`

**Interfaces:**
- Consumes: `DocumentTemplate` (T1).
- Produces: `RenderAsync`, consumed by T4.

- [ ] **Step 1: Write renderer tests first**

Each placeholder kind; block over `OneToMany` and over `ManyToMany`; empty collection emits nothing including block literals; null FK; deleted target row; `{{` escape; each scalar type's invariant rendering; array joining with `", "`; batching (three placeholders on one relation cause one fetch — assert with a substituted `IEntityRepository`); tenant scoping (a related row in another tenant must not render); **identical output across two fetches returning the same rows in different orders**; and a relation declaring an explicit non-conventional `foreignKey`.

- [ ] **Step 2: Implement scalar rendering**

Fixed per type, culture-invariant (Global Constraints): `string` verbatim; `Guid` lowercase `D`; `bool` → `true`/`false`; numerics `InvariantCulture` round-trip with no group separators; `DateTime`/`DateTimeOffset` ISO 8601; arrays joined with `", "` element-wise by the same rules; null/missing/deleted/empty → empty string.

- [ ] **Step 3: Implement relation resolution**

```csharp
Task<string> RenderAsync(
    SchemaDescriptor schema, JsonElement payload, string tenantId, CancellationToken ct)
```

Collaborators: `SchemaRegistry`, `IEntityRepository`. Per kind — `{Rel.Prop}` and `{#Rel}` over `ManyToMany` read the payload key named by `relation.ForeignKey` and issue one `FetchManyByKeysAsync`; `{#Rel}` over `OneToMany` issues one `FetchByColumnAsync(targetSchema, relation.ForeignKey, key)`. Group placeholders by target relation so each relation costs one fetch. Every call passes `tenantScoped: true, tenantId`.

Add a private `ExtractString(JsonElement, string)` matching the existing copies (`EnrichmentConsumer.cs:330`, `IntelligenceStoreConsumer.cs:655`) rather than refactoring those files.

- [ ] **Step 4: Sort block rows before iterating**

Order by the target type's key column, ascending, after the fetch. `FetchManyByKeysAsync` projects the key as `KeyedRow.Key`; `FetchByColumnAsync` returns row JSON only, so extract the target's `KeyColumn.Name` from each row. UUID keys are unique, so this is a total order with no ties.

- [ ] **Step 5: Run tests and commit**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Server/Iverson.Api/Consumers/DocumentRenderer.cs Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRendererTests.cs
git commit -m "add DocumentRenderer for templated document text"
```

---

### Task 4: Ingest hook and the orphaned-chunk fix

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs`
- Modify: `Iverson.Server/Iverson.Vector/IntelligenceFilterBuilder.cs`
- Test: additions to `Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs`

**Interfaces:**
- Consumes: `DocumentRenderer` (T3), synthetic chunk field (T1).

- [ ] **Step 1: Write tests first**

A type with a template lands `document_vector` chunks in `{collection}_chunks` and is retrievable via `SearchChunks(property: "document")`. A template-only type routes to `Intelligence`. **Orphan fix:** a chunk field whose text shrinks from many chunks to few leaves no orphaned points, and the delete does not disturb another chunk field's points on the same parent.

- [ ] **Step 2: Add the parent+field predicate**

Beside `MatchParentId` (`IntelligenceFilterBuilder.cs:57`), using the same `Conditions.MatchKeyword` primitive against `parent_id` and `field`.

- [ ] **Step 3: Delete stale chunk points before the upsert loop**

Inside the `foreach (var cf in schema.ChunkFields)` loop, before writing that field's points, `DeleteByFilterAsync` with the parent+field predicate. Field scoping is required — a parent-only delete would destroy other chunk fields' points on every write.

- [ ] **Step 4: Add the render hook**

Replace the loop's text resolution (`IntelligenceStoreConsumer.cs:183`) with a branch: when `cf.PropertyName == "Document"`, call `DocumentRenderer.RenderAsync` with the schema, payload, and the already-derived authoritative tenant value; otherwise `ExtractString` as today. Everything after text resolution is untouched.

- [ ] **Step 5: Register `DocumentRenderer` in DI and run tests**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Server/Iverson.Api/Consumers/IntelligenceStoreConsumer.cs Iverson.Server/Iverson.Vector/IntelligenceFilterBuilder.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Consumers/IntelligenceStoreConsumerTests.cs
git commit -m "render templated documents at ingest and delete stale chunk points"
```

---

### Task 5: Event fields and prior-payload propagation

**Files:**
- Modify: `Iverson.Server/Iverson.Events/EntityEvent.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/OutboxPublisher.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs`, `ObjectPersistenceGrpcService.cs`
- Test: additions to `Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Produces: `PriorPayloadJson` (consumed by T7), `SuppressRerenderCascade` (set by T8, read by T7).

- [ ] **Step 1: Add two trailing optional members to `EntityEvent`**

`string? PriorPayloadJson = null` and `bool SuppressRerenderCascade = false`, after `TargetStores`. All four existing construction sites are positional and unaffected (`OutboxPublisher.cs:45`, `ReconciliationService.cs:45`, `:116`, `:158`).

- [ ] **Step 2: Thread the prior payload through `PublishAsync`**

Add `string? priorPayloadJson = null` before the trailing `CancellationToken ct = default` on both the interface and implementation (`OutboxPublisher.cs:9-18`).

- [ ] **Step 3: Pass the already-fetched prior row from both update paths**

`ObjectMappingGrpcService.cs:335` and `ObjectPersistenceGrpcService.cs:102` already fetch `existingRowJson` for write authorization — pass it. No new query.

- [ ] **Step 4: Test and commit**

Assert an `Updated` event carries the pre-update row JSON, and that a `Created` event carries null.

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Server/Iverson.Events/EntityEvent.cs Iverson.Server/Iverson.Api/Grpc/ Iverson.Server/Iverson.Api.Tests/Grpc/
git commit -m "carry prior payload and rerender-cascade flag on entity events"
```

---

### Task 6: Re-render queue table and repository

**Files:**
- Create: `Iverson.Server/Iverson.Sql/DocumentRerenderQueueRepository.cs`, `DocumentRerenderQueueRow.cs`
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, `EntityRepository.cs`
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Test: `Iverson.Api.Tests/Reconciliation/DocumentRerenderQueuePostgresIntegrationTests.cs`

**Interfaces:**
- Produces: `IDocumentRerenderQueueRepository` (consumed by T7, T8, T9); `FetchKeysAndTenantsPagedAsync` (consumed by T8).

- [ ] **Step 1: Write the Postgres integration tests first**

Follow `ReconciliationQueuePostgresIntegrationTests.cs`. **Assert on row count, not behavior** — a behavioral assertion passes trivially against a table with no constraints: a second insert of the same `(tenant, type, key)` leaves exactly one row; a second type-level enqueue for the same type leaves exactly one row.

- [ ] **Step 2: Bootstrap the table with raw DDL**

`EnsureTableAsync` following `EnrichmentStateRepository.cs:5-16`. **Not** `ApplySchemaAsync` — it cannot create constraints or indexes (Global Constraints).

```sql
CREATE TABLE IF NOT EXISTS document_rerender_queue (
    id              uuid PRIMARY KEY,
    tenant_id       TEXT,
    type_name       TEXT NOT NULL,
    entity_key      TEXT,
    cursor          TEXT,
    enqueued_at     TIMESTAMPTZ NOT NULL,
    attempts        INTEGER NOT NULL,
    last_error      TEXT,
    last_attempt_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_document_rerender_queue_entity
    ON document_rerender_queue (tenant_id, type_name, entity_key)
    WHERE entity_key IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_document_rerender_queue_type
    ON document_rerender_queue (type_name)
    WHERE entity_key IS NULL;
```

- [ ] **Step 3: Implement the repository**

Interface in `IRecordStoreRoles.cs` beside `IReconciliationQueueRepository`; implementation in `Iverson.Sql/`. Methods: `EnsureTableAsync`, `EnqueueEntityAsync`, `EnqueueTypeAsync`, `PollAsync(batchSize)`, `AdvanceCursorAsync`, `RecordFailureAsync`, `DeleteRowAsync`, `CountPendingAsync`. Inserts use a target-less `ON CONFLICT DO NOTHING`, which considers every unique index on the table including partial ones.

- [ ] **Step 4: Add `FetchKeysAndTenantsPagedAsync` to `EntityRepository`**

Keyset pagination ordered by key, for type-level expansion. Signature follows the executor shape `(sql, param, tenantScoped, tenantId)` and the method follows `IEntityRepository`'s existing style. Reads across tenants (unscoped), like `FetchAllAsync` — a type-level row means "all tenants".

- [ ] **Step 5: Bootstrap and register in `Program.cs`**

Add the `EnsureTableAsync()` call to the plumbing-table block at `Program.cs:404-412`, and register the repository in DI beside `IReconciliationQueueRepository` (`Program.cs:204-206`).

- [ ] **Step 6: Run tests and commit**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Server/Iverson.Sql/ Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Reconciliation/DocumentRerenderQueuePostgresIntegrationTests.cs
git commit -m "add document rerender queue table and repository"
```

---

### Task 7: Reverse-dependency index and `DocumentRerenderConsumer`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs`
- Modify: `Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs`
- Modify: `Iverson.Server/Iverson.Sql/IRecordStoreRoles.cs`, `EntityRepository.cs`
- Test: `Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs`

**Interfaces:**
- Consumes: queue repository (T6); `PriorPayloadJson` / `SuppressRerenderCascade` (T5); parsed templates (T1).

- [ ] **Step 1: Write consumer tests first**

One per relation direction proving the correct owning keys are found; `Created`/`Updated`/`Deleted` all trigger; FK reassignment enqueues **both** parents; `SuppressRerenderCascade` breaks the loop; reverse lookups are tenant-scoped; a relation declaring an explicit non-conventional `foreignKey`.

- [ ] **Step 2: Build the reverse-dependency index on `SchemaRegistry`**

Map target type → `(declaringType, relation)` pairs whose templates reference it, derived from `SchemaRegistry.All`. Rebuild at both mutation points: `LoadAsync` (`SchemaRegistry.cs:20`) and `RegisterAsync` (`SchemaRegistry.cs:47`).

- [ ] **Step 3: Add array containment to `EntityRepository`**

For the `ManyToMany` reverse lookup, using `@>` against the FK array column. Pass the key as a `Guid[]`, never `string[]` — Npgsql sends `string[]` as `text[]` and the uuid index is skipped (`EntityRepository.cs:15-20`).

- [ ] **Step 4: Implement the consumer**

`ConsumerResilience.RunWithRestartAsync` over `EntityTopics.Events` with its own `GroupId`. Ignore any event with `SuppressRerenderCascade`. For each dependent relation: `ManyToOne`/`OneToOne` → `FetchByColumnAsync(declaringSchema, relation.ForeignKey, changedKey)`; `OneToMany` → read the owning key from the payload under `relation.ForeignKey`, and when `PriorPayloadJson` shows a different FK, enqueue both parents; `ManyToMany` → array containment. Enqueue per-entity rows.

- [ ] **Step 5: Run tests and commit**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Server/Iverson.Api/Consumers/DocumentRerenderConsumer.cs Iverson.Server/Iverson.Api/Schema/SchemaRegistry.cs Iverson.Server/Iverson.Sql/ Iverson.Server/Iverson.Api.Tests/Consumers/DocumentRerenderConsumerTests.cs
git commit -m "detect related-entity changes and enqueue document re-renders"
```

---

### Task 8: Queue worker and telemetry

**Files:**
- Create: `Iverson.Server/Iverson.Api/Reconciliation/DocumentRerenderQueueWorker.cs`, `DocumentRerenderOptions.cs`
- Modify: `Iverson.Server/Iverson.Api/Reconciliation/ReconciliationTelemetry.cs`, `Program.cs`
- Test: `Iverson.Api.Tests/Reconciliation/DocumentRerenderQueueWorkerTests.cs`

**Interfaces:**
- Consumes: queue repository and paged read (T6); `SuppressRerenderCascade` (T5).

- [ ] **Step 1: Write worker tests first**

Batch bounding; vanished-row drop; re-fetch produces current state; failure recording; republished events carry `SuppressRerenderCascade = true` and `StoreTarget.Intelligence` only; type-level expansion pages in key order, carries each row's own tenant, and deletes the type-level row on a short page.

- [ ] **Step 2: Implement the worker**

Mirrors `ReconciliationQueueWorker.cs:5-27`. Per tick: drain a bounded batch of per-entity rows, re-fetching each row before publishing (as `ReconciliationService.cs:100` does) so the event carries current state and a vanished row is dropped rather than resurrected; publish via `IEventProducer.ProduceAsync`; delete on success, `RecordFailureAsync` on error.

- [ ] **Step 3: Implement type-level expansion**

Read the next page of `(key, tenant)` pairs after `Cursor` ordered by key, insert one per-entity row for each, advance `Cursor` to the page's last key, and delete the type-level row when a page comes back short. Each per-entity row's tenant comes from the scanned row's own tenant column.

- [ ] **Step 4: Add the queue-depth gauge**

Beside `ReconciliationQueueDepth` in `ReconciliationTelemetry.cs:12-36`, refreshed on the worker's poll cadence. No OTel wiring change — the meter is already registered (`Program.cs:68`).

- [ ] **Step 5: Register both hosted services**

Inside the `workloadRole == "worker"` block (`Program.cs:250-255`) — the worker and Task 7's consumer.

- [ ] **Step 6: Run tests and commit**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Server/Iverson.Api/Reconciliation/ Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Reconciliation/DocumentRerenderQueueWorkerTests.cs
git commit -m "drain document rerender queue on a throttled worker"
```

---

### Task 9: Backfill on template add or change

**Files:**
- Modify: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Test: additions to `Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs`

**Interfaces:**
- Consumes: queue repository (T6); parsed template on the descriptor (T1).

- [ ] **Step 1: Write tests first**

Registering a type whose template differs from the persisted one enqueues exactly one type-level row; re-registering with an identical template enqueues none; registering a type with no template enqueues none.

- [ ] **Step 2: Compare and enqueue**

After validation succeeds and before `registry.RegisterAsync`, compare the newly parsed template against the currently registered descriptor's (`registry.Get(typeName)`). On any difference — including a template newly added — call `EnqueueTypeAsync`. A changed template invalidates every document of that type, because the rendered text is derived data with no stored copy.

- [ ] **Step 3: Run tests and commit**

```bash
dotnet test Iverson.Server/Iverson.Server.slnx
git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api.Tests/Schema/DocumentTemplateValidationTests.cs
git commit -m "enqueue a type-level backfill when a document template changes"
```

---

## Tasks NOT in this plan

Inherited from the spec's "Out of scope" section. A new spec → plan cycle is required to add any of these.

- Client declaration surface in .NET, Java, Python, Go, TypeScript. The proto field ships now because the server reads it; until a client emits it, the feature is dormant and is exercised by tests constructing `SchemaRequest` directly. Go is the notable open question, having no class-level annotation construct.
- Two-hop placeholders, nested blocks, format specifiers, expressions.

## Known issues inherited from spec

These exist in the implementation by design — accepted during brainstorming.

**Reconciliation replay loses FK-reassignment detection.** `ReconciliationService` republishes `Updated` with no prior payload. If a fast-path publish fails and the write is replayed from the outbox, an FK reassignment's *old* parent is not enqueued, and its document stays stale until something else touches it. Accepted rather than building a second mechanism for it.

**The document is invisible to the `GetSchema` agent-facing catalog**, which projects per column. Deliberate: it is a chunk source, not a queryable field.

**Field-restricted types cannot have documents.** A template referencing any `FieldPermission`-carrying property is rejected at registration. Accepted as the price of `Document` being unrestrictable-and-always-readable.
