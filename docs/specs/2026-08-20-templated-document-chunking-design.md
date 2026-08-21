# Templated Document Chunking

**Date:** 2026-08-20
**Status:** Design approved, not implemented
**Scope:** Server only. Client declaration surface is deferred until the client conformance harness is complete.

## Problem

`[IversonChunk]` chunks a single string property. There is no way to build a chunk source
from *several* properties of an entity, or from an entity together with its relations. A
retrieval corpus often wants a coherent per-entity document — title, author, tags, body
rendered as prose — rather than one isolated field.

This design adds a class-level document template. The server renders the template against
the entity payload and its one-hop relations, then chunks and embeds the result through the
existing chunk pipeline. When a related entity changes, affected documents are re-rendered
through a throttled queue.

## Scope

In scope (server):

- A type-level template on `TypeDescriptor`, validated at schema registration.
- A `DocumentRenderer` that resolves placeholders against the payload and one-hop relations.
- Ingest wiring: the rendered document is a synthetic chunk field named `Document`.
- Invalidation: a dedicated consumer detects related-entity changes and enqueues re-renders.
- A throttled queue and worker that drain re-renders as low-priority work.
- A fix for orphaned chunk points on re-write (see "Folded-in fix").

Out of scope:

- Client declaration surface in .NET, Java, Python, Go, TypeScript. The proto field ships
  now because the server reads it; until a client emits it, the feature is dormant and is
  exercised by tests constructing `SchemaRequest` directly. Go is the notable open question,
  having no class-level annotation construct.
- Two-hop placeholders, nested blocks, format specifiers, expressions.

## Declaration

The .NET attribute below is illustrative — it defines the shape the template takes and the
grammar the server validates, but **it is not built by this design**. The declaration surface
in all five clients is deferred (see "Scope"). What ships here is the server's ability to
accept, validate, render, and re-render a template that arrives on `TypeDescriptor`.

```csharp
[IversonEntity]
[IversonDocument("""
    # {Title}
    By {Author.Name}, published {PublishedAt}.

    {#Tags}
    ## Tag: {Name}
    {Description}

    {/Tags}
    {Body}
    """, maxTokens: 512, overlap: 64)]
public class Article { ... }
```

The attribute carries the same knobs `IversonChunkAttribute` carries — `MaxTokens`,
`Overlap`, `Contextual` — because the rendered document flows into the identical chunking
path.

### Placeholder grammar

| Form | Meaning |
|---|---|
| `{Prop}` | A declared scalar property on the declaring type |
| `{Rel.Prop}` | A `ManyToOne`/`OneToOne` relation, then a scalar on the target type |
| `{#Rel}` … `{/Rel}` | A block over a `OneToMany`/`ManyToMany` relation, emitted once per related row |
| `{{` | A literal `{` |

Inside a block, a bare `{Prop}` resolves against the *related row*, not the declaring
entity. A block over zero rows emits nothing at all, including its literal text.

Blocks do not nest, and the one-hop rule holds inside a block: `{Name}` yes, `{Owner.Name}`
no. Two hops would mean an N×M fetch per entity and a substantially larger invalidation
graph.

**Block iteration order is by the target type's key column, ascending**, sorted in the
renderer after the fetch. Neither `FetchByColumnAsync` nor `FetchManyByKeysAsync` carries an
`ORDER BY`, so the order Postgres returns is unspecified and can change after a plan flip or
a vacuum. Without a total order the same unchanged data re-renders as different text,
producing different chunk boundaries and different vectors on every re-render — which the
re-render path triggers on every related-entity change. The key is UUID and unique, so it is
a total order with no ties, and sorting in `DocumentRenderer` needs no new repository
surface.

Separators are literal, so the last row of a block keeps its trailing text:
`{#Tags}{Name}, {/Tags}` yields `a, b, c, `. This is accepted rather than solved with
join/last-item machinery — a prose block naturally ends in a newline, where it does not
matter.

### Scalar rendering

Rendering is fixed per type and culture-invariant. No format specifiers, no configuration.

| Type | Rendering |
|---|---|
| `string` | verbatim |
| `Guid` | lowercase `D` format |
| `bool` | `true` / `false` |
| numeric | `InvariantCulture` round-trip, no group separators |
| `DateTime` / `DateTimeOffset` | ISO 8601 |
| array (`Guid[]`, `string[]`) | elements joined with `", "`, each by the rule above |
| null / missing / deleted target / empty collection | empty string |

The rendered text determines the vector, so the same row must render byte-identically on any
node at any time; a locale-dependent render would make re-embedding non-idempotent and
silently shift search results.

Nulls and missing values render as empty rather than failing. A null FK, a deleted target
row, and an empty collection are ordinary states — failing ingest over them would mean a
deleted tag permanently dead-letters its articles.

Enrichment-produced fields (`[IversonSummary]`, `[IversonKeywords]`, `[IversonExtracted]`)
are ordinary scalars in a template. `{Summary}` renders empty on first ingest and correctly
after `EnrichmentConsumer`'s writeback republishes the entity, because **an entity's own
document always re-renders through the normal ingest path on its own `Updated` event.** The
re-render queue is exclusively for *dependents*.

## Wire contract

`object_mapping.proto`, `message TypeDescriptor` (next free field number is 7):

```proto
string document_template   = 7;   // [IversonDocument] template; empty = none
int32  document_max_tokens = 8;
int32  document_overlap    = 9;
bool   document_contextual = 10;
```

Type-level, not on `PropertyDescriptor` where `is_chunk` lives.

## Schema registration

Validation lives in `SchemaRegistrationOrchestrator`, beside `ValidateFieldReference` and
`ValidateEnrichmentTargets`.

**Validation runs as a second pass, after the `RootType.Concat(Dependents)` loop has
registered every type in the request.** Validating `{Author.Name}` inside the loop can run
before `Author` is registered, since dependents are processed after the root.

`ValidateDocumentTemplate` parses the template once and rejects:

- Unparseable placeholders, unclosed or nested blocks.
- `{Prop}` naming an undeclared property.
- `{Rel.Prop}` where `Rel` is not a declared relation, or `Prop` is not declared on the
  target type.
- `{Rel.Prop}` on a collection relation, or `{#Rel}` on a single-valued relation.
- A template with zero placeholders.
- A template on a type where any other chunk field derives the same named vector — that is,
  where `PropertyName.ToSnakeCase()` collides. `ToSnakeCase` lowercases every character, so
  `Document`, `document`, and `DOCUMENT` all derive `document_vector`; comparing property
  spellings rather than derived names would let the lowercase form through and produce
  duplicate `document_vector` entries in the chunks collection and duplicate
  `document_centroid` entries in the object collection. (Note: this is a *named vector*
  collision, not a collision with the reserved chunk payload keys `text`, `parent_id`,
  `field`, `chunk_index`, which are a separate concern.)
- A template referencing any property that carries a `FieldPermission`, on the declaring
  type or on a one-hop target. See "Authorization" below.
- Registering a type that would break a dependent's template — re-registering `Author`
  without `Name` while an `Article` template references `{Author.Name}` fails with
  `FailedPrecondition` naming the dependent type and placeholder. This matches the existing
  `SchemaDriftPolicy.Throw` stance: fail loudly at registration rather than degrade
  silently at ingest.

The parse produces a `DocumentTemplate` — literal segments and typed placeholders — stored
on `SchemaDescriptor` and persisted with the rest of the schema. Rendering walks that
structure; the template string is never reparsed on the ingest path.

**New `SchemaDescriptor` members must be nullable or defaulted, never `required`.**
`SchemaRegistry.LoadAsync` deserializes pre-change `_iverson_schema` rows, and a required
member missing from legacy JSON throws at startup.

### Synthetic chunk field

`SchemaBuilder.BuildDescriptor` appends a `ChunkDescriptor` named `Document` to the existing
`ChunkFields` list, with `ModelId` and `Dimension` from `IEmbeddingService` exactly as real
chunk fields get them, and `MaxTokens`/`Overlap`/`Contextual` from the new type-level proto
fields.

Everything downstream then works through existing, already-tested code:

| Consumer | Effect of the synthetic entry |
|---|---|
| `ToChunkCollectionSchema` | `document_vector` in the chunks collection — free |
| `ToCollectionSchema` | `document_centroid` on the object point — free |
| `ObjectSearchGrpcService.SearchChunks` | routes by property name; `property: "document"` works |
| `StoreTargeting.HasVectorOrChunkFields` | a template-only type correctly routes to `Intelligence` — required |
| `ObjectMappingGrpcService` `IsChunk` | projects per column; synthetic field is absent, so the document does not appear in the `GetSchema` catalog. Deliberate — it is a chunk source, not a queryable field. |
| `EnrichmentConsumer.BuildSourceText` | extracts by property name from the row; the synthetic field yields nothing, so the enrichment source hash ignores the document. Correct: the document is derived from the very fields already hashed. |
| `RowFieldAuthorizationEvaluator` | see "Authorization" |

The only document-specific code is rendering the text that feeds this field.

## Authorization

Two rules, which must ship together:

1. **`Document` cannot be named by a `FieldPermission`.** Registration rejects it: it is not
   a class property, and a permission on it would be accepted and completely inert, since
   the document is never returned in a read. Someone restricting it would believe they had
   protected something.
2. **`Document` is always present in `decision.AllowedFields`.** `SearchChunks` authorizes
   via `AllowedFields.Contains(chunkDesc.PropertyName)`, and `AllowedFields` derives from a
   `ChunkFields`-inclusive set. Removing `Document` from the known-field set without this
   rule would make document search fail for every caller as soon as the type declares any
   `FieldPermission`.

Because `Document` is unrestrictable, a template must not be able to launder restricted
text into it. Hence the registration rule above: **a template referencing any property that
carries a `FieldPermission` is rejected**, on the declaring type or on a one-hop target. A
document therefore has no restricted sources by construction, "always readable" is sound
rather than a hole, and the failure is a loud registration error instead of a silent
read-time leak.

Cost, accepted: a field-restricted type cannot have a document until the restriction is
dropped.

## Rendering

New service `DocumentRenderer` in `Iverson.Api`, in its own file — a distinct responsibility
from the consumer, and unit-testable without Kafka or Qdrant.

```csharp
Task<string> RenderAsync(
    SchemaDescriptor schema, JsonElement payload, string tenantId, CancellationToken ct)
```

Two collaborators: `SchemaRegistry` (resolving relation target schemas) and
`IEntityRepository` (fetching related rows). It returns a string and knows nothing about
chunking, embedding, or Qdrant.

Resolution per placeholder kind:

| Kind | Resolution |
|---|---|
| `{Prop}` | Read from the already-deserialized event payload. No I/O. |
| `{Rel.Prop}` (`ManyToOne`/`OneToOne`) | Read the payload key named by `relation.ForeignKey`, one `FetchManyByKeysAsync` on the target schema. |
| `{#Rel}` over `ManyToMany` | Read the FK array named by `relation.ForeignKey`, one `FetchManyByKeysAsync`. |
| `{#Rel}` over `OneToMany` | FK lives on the related row: one `FetchByColumnAsync(targetSchema, relation.ForeignKey, key)`. |

Fetches for the same target type are batched into a single call, so three placeholders on
`Author` cost one query.

**All fetches are tenant-scoped**, using the *authoritative* tenant value the consumer
already derives from the Postgres row rather than the unsigned event payload. The renderer
must not become a path by which one tenant's text reaches another tenant's vectors.

## Ingest

In `IntelligenceStoreConsumer.HandleAsync`, the chunk loop's text resolution gains one
branch: if `cf.PropertyName == "Document"`, render the template; otherwise
`ExtractString(payload, cf.PropertyName)` as today.

This hook is required, not cosmetic. The loop currently does
`ExtractString(payload, cf.PropertyName)` and `continue`s when the result is empty, so a
synthetic field with no backing column would be silently skipped. Everything after text
resolution — chunk splitting, contextual prefixing, embedding, centroid computation, point
id, upsert — is untouched.

### Folded-in fix: orphaned chunk points

Chunk points are deleted by parent filter only in `HandleDeleteAsync`. The update path
upserts by deterministic `ComputeChunkPointId(pointId, field, chunkIndex)` with no prior
delete, so **any chunk field whose text shrinks leaves orphaned points behind — still
searchable, with stale text, indefinitely.**

This is a pre-existing bug affecting `[IversonChunk]` fields generally, not something this
design introduces. It is folded in because documents re-render on every related-entity
change rather than only on direct edits, which turns rare orphan accumulation into routine
orphan accumulation.

Fix: delete chunk points by parent *and field* before the upsert loop for that field, using
the existing `DeleteByFilterAsync` and an extended `IntelligenceFilterBuilder` predicate.
Scoping the delete to the field matters — a parent-only delete would destroy the other chunk
fields' points on every write.

## Invalidation

### Reverse-dependency index

`SchemaRegistry` derives, at registration and on schema refresh, a map from *target type* to
the `(declaringType, relation)` pairs whose templates reference it: `Author` →
`[(Article, Author)]`. Built from `SchemaRegistry.All`, already in memory, so the lookup on
the event path is a dictionary hit.

### `DocumentRerenderConsumer`

A dedicated consumer on `EntityTopics.Events` with its own `GroupId`. Deliberately not
folded into `IntelligenceStoreConsumer`: reverse lookups are database work that must not add
latency to primary vector ingest, a separate group makes its lag independently observable,
and its failures do not dead-letter real ingest. This matches the three consumers already
sharing that topic.

`Created`, `Updated`, and `Deleted` are handled identically for all four relation kinds. A
newly created `UserArticle` changes its article's document, so collection relations must
trigger on `Created`; uniform handling is smaller than conditioning per kind, and the
reverse lookup simply returns nothing where a create cannot yet have dependents.

Owning keys, per relation kind:

| Relation on the declaring type | Lookup |
|---|---|
| `ManyToOne` / `OneToOne` | `FetchByColumnAsync(declaringSchema, relation.ForeignKey, changedKey)` |
| `OneToMany` | FK is on the changed row itself — read the owning key from the event payload under `relation.ForeignKey`. No query. |
| `ManyToMany` | Array containment (`WHERE tag_ids @> ARRAY[…]`). **New repository surface** — `FetchByColumnAsync` does not do containment, and nothing in `Iverson.Sql` does today. |

**Every FK reference above is `RelationDescriptor.ForeignKey`, never a `{TypeName}Id` string
built from the convention.** The convention is only a default: `ManyToOne`/`OneToMany` both
accept an explicit override, and the descriptor carries the resolved name. A
convention-derived lookup queries a non-existent column on any type that overrides its FK —
and on the `OneToMany` payload read it fails silently, leaving the parent's document
permanently un-re-rendered.

**FK reassignment.** `EntityEvent` gains a nullable `PriorPayloadJson`, populated on
`Updated`. When a `OneToMany` child's FK differs between prior and current, *both* parents
are enqueued. Both update paths already fetch the prior row for write authorization
(`ObjectMappingGrpcService`, `ObjectPersistenceGrpcService`), so this costs no extra query.
Used for nothing else: a `ManyToOne` or `ManyToMany` FK change is a change to the declaring
entity, which re-renders through its own event.

**Loop breaker.** `EntityEvent` gains `bool SuppressRerenderCascade = false`. The queue
worker sets it on everything it republishes; `DocumentRerenderConsumer` ignores any event
carrying it. Without this, two types whose templates reference each other re-render each
other forever, and even the single-type case amplifies once per hop.

### Queue

New table `document_rerender_queue`, modeled on `ReconciliationSchema` and bootstrapped the
same way (`ApplySchemaAsync` at startup, not through the proto pipeline):

`Id`, `TenantId`, `TypeName`, `EntityKey`, `Cursor`, `EnqueuedAt`, `Attempts`, `LastError`,
`LastAttemptAt`.

A row takes one of two forms. A **per-entity row** carries `TenantId` and `EntityKey` and
names one document to re-render; it is constrained by **`UNIQUE (TenantId, TypeName,
EntityKey)` with `ON CONFLICT DO NOTHING`**. A **type-level row** carries `TypeName` with
`TenantId`, `EntityKey`, and `Cursor` null, and means "every row of this type, all tenants";
it is constrained by a partial unique index on `(TypeName) WHERE EntityKey IS NULL`. The
partial index is required rather than incidental: Postgres treats NULLs as distinct in a
plain unique constraint, so `UNIQUE (TenantId, TypeName, EntityKey)` alone would admit
unlimited duplicate type-level rows.

The per-entity constraint is the primary throttle. An author who edits their bio five times in a
minute collapses to one pending row per article, and a burst that outruns the worker
coalesces in the table rather than piling up as duplicate Kafka messages and duplicate
embeddings.

### `DocumentRerenderQueueWorker`

Mirrors `ReconciliationQueueWorker`: `ConsumerResilience.RunWithRestartAsync`, poll interval
and batch size from a small options class, drain a bounded batch per tick, republish an
`Intelligence`-only `Updated` event per row with `SuppressRerenderCascade = true`, delete
the row on success, record failure on error.

A tick drains a bounded batch of per-entity rows as above. A type-level row is instead
*expanded*: the worker reads the next page of `(key, tenant)` pairs after `Cursor`, ordered
by key, inserts one per-entity row for each (`ON CONFLICT DO NOTHING`), and advances
`Cursor` to the page's last key — deleting the type-level row when a page comes back short.
Each per-entity row's `TenantId` comes from the scanned row's own tenant column, so the
expansion needs no tenant list. Backfill therefore enters the queue at the same bounded rate
as everything else, and registration stays O(1). The paged read is new repository surface,
alongside the `ManyToMany` array-containment method above.

**Re-fetch the row before republishing**, as `ReconciliationService.ProcessOneAsync` does: a
vanished row is dropped rather than resurrected, and the republished event always carries
current state, so a queued re-render can never replay a stale payload over a newer write.

Throughput is capped at `batchSize / pollInterval` by construction — tuning is a config
change, not a code change.

Both the consumer and the worker are registered inside the `workloadRole == "worker"` block
in `Program.cs`, like every other consumer.

### Telemetry

A `document_rerender.queue_depth` observable gauge alongside
`ReconciliationTelemetry.ReconciliationQueueDepth`, refreshed on the worker's poll cadence.
A silently growing re-render backlog means stale vectors with no other symptom.

## Backfill

Adding a template to a type that already has rows would otherwise produce documents only for
rows written after registration, which reads as the feature doing nothing.

Schema registration compares the parsed template against the persisted schema and enqueues
the whole type for re-render on any difference. A *changed* template matters as much as a
new one: the rendered text is derived data with no stored copy, so a template edit
invalidates every document of that type. Registration inserts a single type-level queue row;
the worker expands it a page at a time, so the throttle governs the backfill exactly as it
governs ordinary re-renders.

## Storage

The rendered document is **not** persisted as a column. It is rendered transiently at ingest
and exists only as chunk text in `{collection}_chunks`. It is derived data with a re-render
path; a column would be a second copy to keep in sync.

## Testing

Per project convention these must be mutation-tested, not merely green.

**`DocumentRenderer`** — each placeholder kind; block over each collection relation kind;
empty collection; null FK; deleted target; escaped braces; each scalar type's invariant
rendering; array joining; batching (one fetch for three placeholders on one relation);
tenant scoping (a related row in another tenant must not render); identical output across
two fetches returning the same rows in different orders; a relation declaring an explicit
non-conventional `foreignKey`.

**`ValidateDocumentTemplate`** — one test per rejection: undeclared property, undeclared
relation, wrong relation kind for the form used, two-hop, nested block, unclosed block, zero
placeholders, `Document` named-vector collision (including a lowercase `document` property),
`FieldPermission`-carrying source property,
and a dependent-breaking re-registration of a target type. Plus the ordering test: a
template referencing a type that appears later in `dependents` must validate successfully.

**`DocumentRerenderConsumer`** — one test per relation direction proving the correct owning
keys are found; `Created`/`Updated`/`Deleted` all trigger; FK reassignment enqueues both
parents; `SuppressRerenderCascade` breaks the loop; reverse lookups are tenant-scoped; a
relation declaring an explicit non-conventional `foreignKey`.

**`DocumentRerenderQueueWorker`** — collapse under the unique constraint; batch bounding;
vanished-row drop; re-fetch produces current state; failure recording; type-level expansion
pages in key order, carries each row's own tenant, and deletes the type-level row on a short
page.

**Orphan fix** — a chunk field whose text shrinks from many chunks to few leaves no
orphaned points, and the delete does not disturb other chunk fields' points on the same
parent.

**`IntelligenceStoreConsumer`** — a type with a template lands `document_vector` chunks in
`{collection}_chunks` and is retrievable via `SearchChunks(property: "document")`; a
template-only type (no `[IversonChunk]` property) routes to `Intelligence` at all.

**Authorization** — `SearchChunks(property: "document")` succeeds on a type declaring an
unrelated `FieldPermission` (the regression rule 2 exists to prevent).

## Verified assumptions

All 28 were listed before any verification read, and all were checked against the codebase.

Held as assumed:

- `ChunkDescriptor(PropertyName, MaxTokens, Overlap, ModelId, Dimension, Contextual)` and
  `SchemaDescriptor.ChunkFields` admit a synthetic entry — `SchemaDescriptor.cs:57`.
- `ToChunkCollectionSchema` and `ToCollectionSchema` derive `_vector` and `_centroid` purely
  from `ChunkFields` — `SchemaBuilder.cs:213`, `SchemaBuilder.cs:220`.
- `SearchChunks` routes by property name against `ChunkFields` — `ObjectSearchGrpcService.cs:301`.
- Chunk model id and dimension come from `IEmbeddingService`, not from the proto —
  `SchemaBuilder.cs:66`.
- `StoreTargeting.HasVectorOrChunkFields` routes a chunk-bearing type to `Intelligence` —
  `StoreTargeting.cs:42`.
- `SchemaRegistry.All` exposes every schema for the reverse index — `SchemaRegistry.cs:13`.
- `ReconciliationSchema` / `IReconciliationQueueRepository` / `ReconciliationQueueWorker` /
  `ReconciliationTelemetry` provide the durable-table + throttled-poller pattern.
- `ReconciliationService.ProcessOneAsync` re-fetches before republishing —
  `ReconciliationService.cs:100`.
- `EnrichmentTargets` are declared string properties, hence templatable — `SchemaDescriptor.cs:44`.
- `EntityEvent` has only 4 construction sites outside tests, so additive fields are cheap.
- `Iverson.AdminUI` has no chunk-field dependency.

Changed the design:

- **Reserved chunk payload keys are `text`, `parent_id`, `field`, `chunk_index`**
  (`SchemaBuilder.cs:25`) — `"document"` does not collide with them. The real collision is
  the `document_vector` *named vector*, which is what registration now checks.
- **The chunk loop needs an explicit hook.** It does `ExtractString(payload, cf.PropertyName)`
  and `continue`s on empty (`IntelligenceStoreConsumer.cs:183`), so a synthetic field would
  be silently skipped.
- **Template validation must run after the registration loop.** `RegisterAsync` iterates
  `RootType.Concat(Dependents)`, so a root's reference to a dependent type can be validated
  before that type is registered — `SchemaRegistrationOrchestrator.cs:33`.
- **New `SchemaDescriptor` members must be nullable or defaulted** — `SchemaDescriptor.cs:21`.
- **The consumer and worker are gated on `workloadRole == "worker"`** — `Program.cs:250`.
- **`PriorPayloadJson` is free**: both update paths already fetch the prior row for write
  authorization — `ObjectMappingGrpcService.cs:335`, `ObjectPersistenceGrpcService.cs:102`.
- **No array-containment query exists** anywhere in `Iverson.Sql`, confirming the
  `ManyToMany` reverse lookup needs new repository surface.
- **`SearchChunks` authorizes via `decision.AllowedFields.Contains(...)`**
  (`ObjectSearchGrpcService.cs:312`), which is why `Document` must be unrestrictable *and*
  always allowed — the two rules in "Authorization" must ship together.
- **Chunk points are deleted by filter only in `HandleDeleteAsync`**
  (`IntelligenceStoreConsumer.cs:488`), confirming the orphaned-point bug now folded into
  scope.
- **Neither relation fetch specifies an order** — `EntityRepository.cs:18-27`. Block
  iteration must impose its own total order; see "Placeholder grammar".
- **Relation foreign keys are overridable** — `ManyToOneAttribute.cs:10`,
  `OneToManyAttribute.cs:11`, `object_mapping.proto:73` ("resolved FK column (convention or
  explicit)"). All FK access goes through `RelationDescriptor.ForeignKey`.

## Known limitations

**Reconciliation replay loses FK-reassignment detection.** `ReconciliationService`
republishes `Updated` with no prior payload. If a fast-path publish fails and the write is
replayed from the outbox, an FK reassignment's *old* parent is not enqueued, and its
document stays stale until something else touches it. Accepted rather than building a second
mechanism for it.

**The document is invisible to the `GetSchema` agent-facing catalog**, which projects per
column. Deliberate: it is a chunk source, not a queryable field.

**Field-restricted types cannot have documents.** A template referencing any
`FieldPermission`-carrying property is rejected at registration. Accepted as the price of
`Document` being unrestrictable-and-always-readable.

**The feature is dormant until client work lands.** No client emits `document_template`, so
until the client conformance harness completes and the declaration surface follows, the path
is reachable only from tests.
