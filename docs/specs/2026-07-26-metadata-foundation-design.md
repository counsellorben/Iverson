# Metadata Foundation — Design

**Date:** 2026-07-26
**Status:** Approved (brainstorm + empirical verification complete)
**Sub-project 1 of the metadata / tensor-search initiative**

## Context

Iverson will gain a metadata layer to improve semantic-search precision and reduce
downstream LLM token expenditure (fewer, better, smaller results for RAG consumers and
querying agents). The initiative decomposes into: **(1) this metadata foundation**,
(2) Ollama-based ingest enrichment, (3) server-side tensor re-ranking/fusion scoring
(System.Numerics.Tensors), (4) derived vector signals (centroids/clusters), and
(5) an agent-facing schema/query surface. Pieces 2–5 are out of scope here and
consume what this foundation delivers.

## Goal

Let types declare, in all five client languages:

1. **Metadata fields** — stored scalar fields flagged as semantic-search signals, and
2. **Descriptions** — free-text semantics on types and fields,

with the server persisting both in the schema registry and denormalizing metadata
values onto Qdrant chunk points so they are filterable and readable at search time
without a second store round-trip.

## Design

### 1. Client declaration surface (all 5 languages)

Two new declarations, mirroring each language's existing `is_search_key` idiom:

| Language | Mechanism (verified location) |
|---|---|
| C# | attributes — `Iverson.Client.Attributes` (new `IversonMetadataAttribute`, `IversonDescriptionAttribute`) |
| Java | annotations — `io.iverson.client.annotations` (`@IversonMetadata`, `@IversonDescription`) |
| Python | field-descriptor mechanism in `iverson_client/core.py` |
| Go | struct tags in `iverson/tags.go` (e.g. `iverson:"metadata"`, `iverson_desc:"…"`) |
| TypeScript | decorators in `src/annotations.ts` |

Semantics:

- `[IversonMetadata]` marks a stored **scalar** property as a metadata signal. The
  property remains an ordinary column everywhere; the flag adds chunk-point payload
  denormalization (§4) and registry semantics for future fusion stages.
- `[IversonDescription("…")]` is valid on a type or any property; empty = absent.
- Each `SchemaRegistrar` maps the declarations onto the new proto fields (§2).

Validation (server-side at registration; clients may pre-validate where cheap):
`is_metadata` is **rejected** on embedding, chunk, relation, array (`is_array`),
and large-field properties (large fields: denormalizing up-to-64KB values onto every
chunk point is harmful; arrays: they would land as a single JSON-text blob that
keyword filters cannot match elements of, silently breaking the §4 filterability
guarantee) and is
meaningless on the key/tenant fields (already present in payload / collection routing).

### 2. Proto change — `Iverson.Clients/Common/Proto/object_mapping.proto`

```proto
message PropertyDescriptor {
    // existing fields 1–16 unchanged
    bool   is_metadata = 17;  // [IversonMetadata] present
    string description = 18;  // [IversonDescription] text; empty = none
}
message TypeDescriptor {
    // existing fields 1–5 unchanged
    string description = 6;   // type-level [IversonDescription]
}
```

Additive field numbers only (verified free: PropertyDescriptor tops out at 16,
TypeDescriptor at 5); old clients stay wire-compatible. All five clients regenerate
from this single shared file via their existing generation scripts.

### 3. Server schema model

`SchemaDescriptor` gains three members following the `LargeFieldColumns` pattern —
defaulted, not `required`, so legacy `_iverson_schema` JSON rows still deserialize
(constraint documented in the file itself):

```csharp
public HashSet<string>            MetadataColumns   { get; init; } = [];
public string?                    Description       { get; init; }
public Dictionary<string, string> FieldDescriptions { get; init; } = [];
```

`SchemaBuilder.Build` populates them inside its existing per-property loop
(SchemaBuilder.cs:33–121) and raises the §1 validation errors alongside the existing
search-key/large-field conflict check (:75–79).

### 4. Qdrant chunk-point denormalization

Object-level points already mirror **all** scalar and FK columns into their payload
(IntelligenceStoreConsumer.cs:137–150) — no change there. Chunk points today carry
only `text`, `parent_id`, `field`, `chunk_index`, and owner.

Change: in the chunk-upsert block (IntelligenceStoreConsumer.cs:191–208), each chunk
payload additionally gets one entry per `MetadataColumns` member — camelCase key,
value via the existing `ExtractTypedValue` against the parent object's event payload
(already in scope), nulls omitted. The loop **skips** any `MetadataColumns` member
equal to `schema.Authorization?.OwnerField` (OrdinalIgnoreCase): the consumer already
writes that key from the authoritative Postgres row (IntelligenceStoreConsumer.cs:200–201,
CSR #7), and sourcing it from the unsigned event payload would corrupt chunk-search
row authorization. Metadata payload refreshes whenever the object is re-written,
matching existing payload behavior.

The filter DSL itself needs no change: `IntelligenceFilterBuilder` already filters
arbitrary payload keys with string/bool/number equality. However, the gRPC layer in
front of it, `BuildChunksFilter` (ObjectSearchGrpcService.cs:576–585), currently
rejects any `SearchChunks` filter that is not exactly one EQUALS clause on the
primary-key property. Change: extend `BuildChunksFilter` to also accept EQUALS
clauses whose property is in `schema.MetadataColumns` (camelCase payload key, the
existing `BuildEqualityCondition` value kinds), keeping the primary-key special case;
clauses on any other property are still rejected. This makes chunk metadata
filterable by clients through `SearchChunks`.

### 5. Search-result payload fix (latent bug, folded in)

`IntelligenceVectorService` result mapping reads `Value.StringValue` only
(lines 80 and 112), so every non-string payload value (stored as native
integer/double/bool kinds) currently comes back as an **empty string**. Fix: convert
each payload `Value` kind to its canonical string (`42` → `"42"`, `true` → `"true"`,
`3.5` → `"3.5"`), keeping the `VectorSearchResult` `Dictionary<string, string>` shape
and all downstream consumers untouched. Applies to both `SearchAsync` and
`SearchNamedAsync`.

### 6. Description retrieval — deferred

No schema-read RPC exists today (`RegisterSchema` is the only schema RPC — verified).
Descriptions and metadata flags are stored in the registry and readable in-process by
server-side consumers (the future enrichment/fusion stages). A client-facing
`GetSchema`-style surface is deliberately deferred to the agent-facing sub-project
(#5), which is the piece that needs it. Decision made by Ben 2026-07-26.

### 7. Testing

- **Server** — `SchemaBuilder` tests: proto → descriptor mapping of the new members;
  validation rejections (metadata on embedding/chunk/large/relation/array). Registry
  test: legacy JSON without the new members loads with empty defaults. Consumer tests:
  chunk points carry parent metadata values; object points unchanged; a schema whose
  owner field is metadata-flagged yields chunk points carrying the authoritative
  owner value (not the event-payload value). Search service tests: `SearchChunks`
  accepts an EQUALS clause on a metadata column, still accepts the primary-key
  clause, and still rejects clauses on non-metadata, non-key properties. Vector service
  test: non-string payload values round-trip as canonical strings.
- **Clients** — per-language registrar/annotation tests asserting the new
  declarations produce `is_metadata`/`description` on the wire, mirroring each
  language's existing `is_search_key` tests (verified present in all five).

## Verified assumptions

All verified empirically 2026-07-26:

1. Proto field numbers 17/18 (PropertyDescriptor) and 6 (TypeDescriptor) are free;
   one shared proto file consumed by server and all 5 clients via generation scripts.
2. `SchemaBuilder.Build` is the single proto→descriptor mapping point with an
   extendable validation precedent.
3. `SchemaRegistry` persists descriptors as JSON (`SchemaRegistry.cs:31,51`);
   defaulted members tolerate legacy rows.
4. Upsert payload is a `Dictionary<string, object>`; `ToQdrantValue` covers all
   types `ExtractTypedValue` produces (long/double/bool/string).
5. The parent object's event payload (`JsonElement`) is in scope in the chunk-upsert
   block — denormalization needs no extra fetch.
6. `IntelligenceFilterBuilder` filters arbitrary payload keys — no DSL change.
7. Result payload mapping is `StringValue`-only — the §5 bug is real, in both search
   methods.
8. All five clients have an extendable declaration mechanism with existing tests.
9. **Invalidated:** no schema-read surface exists → §6 deferral decision.
10. Object key and tenant value already reach Qdrant (payload `key`; tenant-scoped
    collection routing); chunk collections are tenant-isolated by name.
11. All 16 `SchemaDescriptor` consumers read only existing members; additive members
    leave SQL/StarRocks DDL, authorization, and the refresh worker unaffected. Bonus
    finding: object points already mirror all scalars (narrowed §4's scope).
12. (Added by round-2 CDR) `BuildChunksFilter` restricts client `SearchChunks`
    filters to a single primary-key EQUALS clause (ObjectSearchGrpcService.cs:576–585)
    — client-facing metadata filterability therefore requires the `BuildChunksFilter`
    extension specified in §4.

## Out of scope

Ingest enrichment via Ollama, tensor re-ranking/fusion, derived vector signals,
agent-facing schema retrieval, and any metadata-only (non-column) fields.
