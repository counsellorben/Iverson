# Ingest Enrichment (Ollama) — Design

Date: 2026-07-26
Status: Approved design, not yet planned or implemented
Part 2 of the metadata / tensor-search initiative.

## Context

Part 1 (metadata foundation, `docs/specs/2026-07-26-metadata-foundation-design.md`)
delivered declared metadata fields, `[IversonMetadata]`/`[IversonDescription]` across
five client languages, and `MetadataColumns`/`Descriptions` in the in-process schema
registry. This part uses a generative Ollama model at ingest time to populate
server-derived fields, improving semantic search and reducing the text a downstream
agent must read.

Parts 3 (tensor re-rank/fusion), 4 (derived vector signals), and 5 (agent-facing
schema surface) remain out of scope.

**Base branch:** this work builds on `metadata-foundation` (part 1), which is
implemented but not yet merged to `main`.

## Goal

Four enrichment outputs, produced server-side at ingest:

1. An object-level summary.
2. A keyword / lexical term list.
3. Extracted structured metadata filling declared fields.
4. A contextual prefix per chunk, conditioning that chunk's embedding.

## 1. Declaration surface

Four new declarations, following part 1's attribute pattern in all five client
languages (.NET, Java, Python, Go, TypeScript):

- `[IversonSummary]` on a string property — server writes an object-level abstract.
- `[IversonKeywords]` on a string property — server writes a comma-separated term list.
- `[IversonExtracted("what to extract")]` on a property — server fills it from the
  object's text, guided by the hint.
- `Contextual = true` on the **existing** `[IversonChunk]` attribute — opts that chunk
  field into contextual prefixes. A modifier on chunking, not a new attribute.

The client declares these properties and leaves them empty. Because they are ordinary
declared columns they reach Postgres, StarRocks, and Qdrant through the existing
projection with no new plumbing — `SchemaBuilder` places them in `ScalarColumns` like
any other scalar (verified: `SchemaBuilder.cs:136`).

**Enrichment source text** is the concatenation of the type's existing
`[IversonEmbedding]` and `[IversonChunk]` properties. There is deliberately no separate
source attribute: those annotations already mark the meaningful text on a type, and a
second way to say the same thing is a knob nobody asked for.

### Proto

Four new fields on `PropertyDescriptor` in `object_mapping.proto`. The message is flat
and already carries chunk config at fields 9–13; part 1 took 17 and 18, so 19–22 are
free and no new message is needed.

```
bool   is_summary_target  = 19;  // [IversonSummary]
bool   is_keywords_target = 20;  // [IversonKeywords]
string extract_hint       = 21;  // [IversonExtracted]; empty = absent
bool   chunk_contextual   = 22;  // [IversonChunk(Contextual = true)]
```

### Registry

`SchemaDescriptor` gains `EnrichmentTargets` (property name → kind + optional hint), and
`ChunkDescriptor` gains `Contextual`.

New members **must be defaulted, not `required`**, and any `HashSet<string>` must
re-apply `StringComparer.OrdinalIgnoreCase` in its `init` accessor. `SchemaRegistry.LoadAsync`
deserializes legacy `_iverson_schema` rows with `System.Text.Json`: a `required` member
missing from legacy JSON throws at startup, and a deserialized `HashSet` silently reverts
to the case-sensitive default comparer. Both hazards are documented in place at
`SchemaDescriptor.cs:21-33`.

### Validation at registration

- Enrichment targets must be text-typed columns.
- They must not be the key, tenant, or owner column.
- `[IversonExtracted]` requires a non-empty hint.

## 2. The enrichment pipeline

`EnrichmentConsumer : BackgroundService`, group id `iverson.consumer.enrichment`,
subscribing to `EntityTopics.Events` and reusing `ConsumerResilience.RunWithRestartAsync` —
the same shape as the two existing consumers, registered alongside them
(`Program.cs:239-240`). It gates on `schema.EnrichmentTargets.Count > 0`; no new
`StoreTarget` flag, because the registry already answers the question.

Per Created/Updated event:

1. Fetch the authoritative row via `IEntityRepository.FetchByKeyAsync` — not the event
   payload, matching the re-derivation `IntelligenceStoreConsumer` already performs for
   owner and tenant values.
2. Build the source text and hash it (SHA-256).
3. Compare against the stored hash. **Equal → stop.**
4. Otherwise call Ollama per target, write back (§3), and store the new hash.

### Loop prevention

Step 3 is the loop breaker. An enrichment writeback modifies only enrichment target
columns, never source text, so the `entity.updated` it republishes hashes identically and
is dropped on the second pass. No event marker has to be threaded through the outbox.

The same check absorbs `ReconciliationService` republishes at no cost.

### Enrichment state table

The hash lives in a server-owned `iverson_enrichment_state` table keyed by
`(tenant_id, type_name, key)`, created by an `EnsureTableAsync` mirroring
`SchemaRegistryRepository.cs:5-8`.

It is deliberately not a column on the client's type: that would put server bookkeeping
into client `Get` responses and StarRocks tables, and would mean mutating the schema of
every enriched type.

It is a **plumbing table**, like the outbox — outside RLS, holding `tenant_id` as ordinary
data rather than as an RLS-enforced boundary. Deletes remove the state row.

## 3. Writeback safety

`IOutboxWriter.UpsertAndEnqueueOutboxAsync` writes an entire payload
(`OutboxWriter.cs:23-28`). Using it here would mean read-modify-write: the enricher reads
the row, spends seconds in the LLM, then writes the whole row back — silently clobbering
any client update that landed in between. That is client data loss, not merely a stale
summary.

So this design adds a targeted `IEntityRepository.UpdateColumnsAsync(schema, key, columns)`
issuing an `UPDATE ... SET` over only the enrichment target columns. `IEntityRepository`
has no such method today (`IRecordStoreRoles.cs:49-56`); it is genuinely new.

### Transaction ordering (mandatory)

The entity update, state upsert, and outbox enqueue run in one transaction, in this order:

1. `EnterTenantScopeAsync(tenantId)` — sets role `iverson_runtime` and the RLS GUC.
2. The targeted `UPDATE` on the entity table.
3. `ExitTenantScopeAsync()`.
4. The `iverson_enrichment_state` upsert and the outbox insert.

Steps 3–4 are not optional. `SET LOCAL ROLE` persists for the remainder of the
transaction, and the outbox and state tables have no `iverson_runtime` grant, so any
plumbing-table statement issued while still in tenant scope fails. `OutboxWriter.cs:42-49`
performs exactly this sequence, and `TenantScopeTransactionExtensions`
(`IRecordStoreRoles.cs:28-47`) documents the hazard.

The republished `entity.updated` then converges StarRocks and Qdrant through the two
existing consumers, both of which are idempotent.

### Authorization

The enricher acts as a system identity with no acting user, passing the tenant value
re-derived from the authoritative row — the pattern `IntelligenceStoreConsumer.cs:106-108`
uses. Because it writes through the repository rather than the gRPC surface,
`EnforceWriteAuthorization` and write-masking do not apply. Client *reads* of enriched
columns remain subject to normal `AllowedFields` field authorization, unchanged, since
enriched columns are ordinary declared properties.

## 4. Contextual chunk prefixes

When a chunk field declares `Contextual = true`, `IntelligenceStoreConsumer` generates a
short situating sentence per chunk and prepends it **to the text it embeds only**. The
chunk payload's `text` key remains the raw chunk, so retrieval still returns clean
passages.

The generated prefix is **not** stored in the chunk payload. `SearchChunks` reads only
`text` and `parent_id` (`ObjectSearchGrpcService.cs:312-313`), so a stored prefix would be
a key written and never read.

The context prompt is conditioned on the object's **summary**, not the full parent
document. This is the difference between affordable and not: on a CPU-only Ollama, feeding
a large parent document into N generations per object is untenable, while a
one-paragraph summary keeps every prefix call small and roughly constant-cost regardless
of document size.

### The two-pass sequence

On first ingest the summary does not exist yet, so prefixes fall back to a truncated slice
of the parent text. The enricher then writes the summary and republishes `entity.updated`,
and the second Intelligence pass regenerates prefixes summary-conditioned. Chunk point IDs
are deterministic (`ComputeChunkPointId`), so the second pass overwrites rather than
duplicating.

The cost is one redundant chunk-embedding pass on first ingest. Accepted deliberately: the
alternative is an ordering constraint between two independent consumers, which is a far
worse thing to introduce than one repeated pass.

## 5. Generative service and infrastructure

A new `IEnrichmentService` alongside `IEmbeddingService` in `Iverson.Embeddings`, wrapping
Ollama `/api/generate` with `format: "json"` so extraction returns parseable structure
rather than prose to regex. Ollama supports both `format: "json"` and JSON-schema
structured outputs on this endpoint (verified against the official API docs).

Config mirrors the existing `EmbeddingServiceOptions` shape (`Section` const, `BaseUrl`,
`ModelId`), registered the same way `AddEmbeddings(cfg)` is at `Program.cs:228`:

- `Enrichment__BaseUrl`
- `Enrichment__ModelId` (default `qwen2.5:3b`)
- `Enrichment__Enabled`
- request timeout

Same `IHttpClientFactory` and `Telemetry.Source` activity pattern the embedding service
already uses.

### Model provisioning

Ollama runs CPU-only today (8 CPU / 16Gi, no GPU nodeSelector or tolerations —
`values.yaml:76-85`) and pulls only `nomic-embed-text`. A small CPU-viable generative model
is pulled alongside it. Adding GPU support to the chart is explicitly out of scope.

- **Helm:** the `pull-model` init container is already a shell script
  (`statefulset.yaml:53-59`); it takes one more `ollama pull` line.
- **docker-compose:** `ollama-init` is a single `curl` argv
  (`docker-compose.yml:96-103`) and must be restructured to issue two pulls.

### Prompts

One prompt per output kind — summary, keywords, extraction, chunk context — held
server-side and not configurable. Prompt templating is not requested and can be added when
a real second use case exists.

## 6. Failure handling

Ollama failures are transient infrastructure failures, not poison messages. A failed
enrichment logs, leaves the state row absent, and returns **without** throwing
`PoisonMessageException`, so the object stays unenriched and is picked up on the next event
or reconciliation pass.

Enrichment must never block or fail an object's projection into the stores. An object with
a null summary is fully functional, merely less searchable.

`Enrichment__Enabled=false` disables the consumer outright, which is also what keeps
existing tests and the LoadTest path unaffected.

## 7. Testing

Following `IntelligenceStoreConsumerTests`:

- Hash-unchanged skips the enrichment pass — the loop breaker, and the single most
  important test in this design.
- Targeted column update preserves a concurrent client edit to another column.
- LLM failure leaves the object intact and unenriched, and does not poison the message.
- Transaction exits tenant scope before touching the state and outbox tables.
- Registration validation rejects non-text targets, key/tenant/owner targets, and an empty
  `[IversonExtracted]` hint.
- Per-language registrar tests for the four new declarations, mirroring part 1's.

## Out of scope

Tensor re-ranking and fusion, derived vector signals, agent-facing schema retrieval, GPU
support in the Ollama chart, configurable prompt templates, and any enrichment output
beyond the four named above.

## Known issues — pre-existing, not addressed here

Neither the Go nor the TypeScript client populates `TypeDescriptor.tenant_field`, which the
proto marks REQUIRED and `SchemaRegistrationOrchestrator` rejects. Schema registration from
those two clients already fails against a current server. This was found during part 1's
execution and is unrelated to enrichment, but it is adjacent: this design adds registrar
work in those same two files, and end-to-end verification of the new declarations from Go
or TypeScript will not be possible until it is fixed. It needs its own task.

## Verified assumptions

Every load-bearing assumption below was checked against the `metadata-foundation` branch
before this spec was written.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `FetchByKeyAsync` exists; no column-update method does | `IRecordStoreRoles.cs:51`; interface at lines 49-56 has no update method |
| A2 | A transaction context can compose entity + state + outbox writes | `IRecordStoreRoles.cs:16-26`; `OutboxWriter.cs:40-56` |
| A3 | RLS permits system-actor writes to all three targets | `TenantScopeTransactionExtensions`, `IRecordStoreRoles.cs:37-47`; **constraint found** — tenant scope must be exited before plumbing-table writes |
| A4 | A third consumer on the same topic is supported | `Program.cs:239-240` |
| A5 | Server-owned tables have a creation mechanism | `SchemaRegistryRepository.cs:5-8`, called from `SchemaRegistry.cs:22` |
| A6 | Proto has room on a per-property descriptor | `object_mapping.proto` — chunk config at 9-13, part 1 at 17-18, 19-22 free |
| A7 | All five clients have an attribute + registrar path | `IversonMetadata` present in .NET, Java, Python, Go, and TypeScript, each with registrar tests |
| A8 | `SchemaDescriptor` is extensible | `SchemaDescriptor.cs:21-33`; **constraint found** — defaulted not `required`, comparer re-applied in `init` |
| A9 | Enrichment columns project automatically | `SchemaBuilder.cs:136` — `ScalarColumns = scalars` |
| A10 | A `context` payload key collides with nothing | `ObjectSearchGrpcService.cs:312-313` reads only `text`/`parent_id`; **changed the design** — the key was dropped as write-only |
| A11 | The extra republish breaks nothing | Engagement upsert and Intelligence deterministic `ComputeChunkPointId` are both idempotent; events keyed by entity key preserve per-key ordering |
| A12 | Field authorization is unaffected | Enricher bypasses gRPC; client reads unchanged (`ObjectSearchGrpcService.cs:252`) |
| A13 | The embedding service pattern is copyable | `EmbeddingServiceOptions`; `Program.cs:228`, `Program.cs:371` |
| A14 | Ollama supports `format: "json"`; models are pullable | Official Ollama `api.md`; `statefulset.yaml:53-59`; **finding** — `docker-compose.yml:96-103` needs restructuring |
| A15 | Part 1 is unmerged; base is `metadata-foundation` | `git worktree list` — `metadata-foundation` @ `eabef05`; `main` @ `30e9db2` has only docs |
