# Strip relation navigation properties from write payloads

**Date:** 2026-08-06
**Status:** Approved, not yet planned

## Problem

A relation is represented on a client entity by two properties: the foreign-key scalar
(`AuthorId`), which has a database column, and the navigation property (`Author`), which does not.
The nav property exists so callers can traverse the graph without a second fetch.

Both the client's `GraphAssembler` and the server's `EntityRelationResolver` *populate* nav
properties on read. Nothing empties them before a write. The .NET client's `StructConverter.ToStruct`
serializes the whole POCO with no property filtering, so the ordinary read-modify-write cycle ships
the entire hydrated related object — or, for collections, every related entity — back to the server
on every write:

```csharp
var post = await client.GetAsync<Post>(id);   // Author now fully hydrated
post.Title = "new title";
await client.UpdateAsync(post);               // sends the entire Author back
```

Nobody wrote code to do this. It falls out of the read path hydrating and the write path having no
symmetric de-hydrating step.

### Current impact

No data corruption: all three stores already ignore nav properties.

| Store | Nav property fate | Evidence |
|---|---|---|
| Postgres | dropped — `json_populate_record` ignores keys with no matching column | `Iverson.Sql/OutboxWriter.cs:27` |
| StarRocks | dropped twice — filtered by `knownCols`, and `JsonValueKind.Object` skipped | `Iverson.StarRocks/EngagementRepository.cs:230-233` |
| Qdrant | never read — only schema-declared vector/scalar/FK fields are iterated | `Iverson.Api/Consumers/IntelligenceStoreConsumer.cs:370-388` |

The real costs are:

1. **Event bloat.** Every fast-path Kafka message carries a duplicate of the related graph. Payload
   size scales with the size of the related graph, not the entity. This is the thread connecting to
   the open StarRocks >64KB dead-letter issue.
2. **Path-dependent event bodies.** The fast path (`OutboxPublisher.cs:45`) publishes the *request*
   payload with nav objects intact; reconciliation (`ReconciliationService.cs:45`) publishes
   `rowJson` re-fetched from Postgres, where they were already dropped. The same entity produces two
   different event bodies depending on which path emitted it. Latent today because every consumer
   ignores those keys, but event content is not reproducible.

## Design

Strip nav properties server-side, in `RelationValidator`, after FK normalization.

### Why `RelationValidator`

It already walks every declared relation, already mutates the payload, and is the single choke point
every payload-carrying write passes through (4 call sites: `ObjectPersistenceGrpcService.cs:43,114`
and `ObjectMappingGrpcService.cs:298,351`).

Two alternatives were rejected. A separate stripping pass at the four call sites duplicates the same
logic four times in two files, and a fifth write path added later would silently skip it. Stripping
inside `StructSerializer.SerializePayload` is the wrong layer — it receives only a `Struct` and has
no schema, so it cannot distinguish a relation property from an ordinary field.

### Stripping

One unconditional removal per relation in the top-level loop, after the `switch`:

```csharp
if (!string.Equals(relation.PropertyName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase))
    StructFieldAccess.RemoveField(payload, relation.PropertyName);
```

Placing it in the top-level loop rather than inside the per-kind methods is deliberate.
`ValidateSingleRelation` has three early `return`s, and `OneToMany` `break`s out of the switch
immediately — a per-branch approach would need four scattered call sites and would most likely miss
`OneToMany`, whose collections are the largest payloads of all.

**The name guard is load-bearing.** `PropertyName` and `ForeignKey` are not guaranteed distinct —
see assumption A7 below. Without the guard, a many-to-many relation whose property name matches its
inferred FK would have its foreign key deleted by the strip.

`StructFieldAccess.RemoveField(Struct, string)` is new, removing the canonical name and its case
variants via the existing `Candidates`, mirroring `SetField`. Clients serialize camelCase (`author`)
while schemas declare PascalCase (`Author`); removing only the declared spelling would leave the
camelCase key in the payload — the exact failure shape behind the duplicate-key crash fixed in
`361dc0c`.

### Conflict detection

A payload carrying both an FK and a nav property that disagree currently resolves silently in the
FK's favour, because both validate methods return as soon as they find an FK. Disagreement becomes
an explicit error. Both methods must therefore examine the nav property even when the FK is present.

**Single relations** (`ManyToOne`, `OneToOne`):

| FK | Nav object | Result |
|---|---|---|
| valid GUID | absent | unchanged |
| valid GUID | resolves to same key | accepted |
| valid GUID | resolves to different key | **error** |
| valid GUID | keyless / malformed | existing nested-validation error |
| absent or `NullValue` | present | normalize into FK |
| absent | absent | existing required/nullable check |

**Collections** (`ManyToMany`): conflict is checked only when the FK list is present *and* the nav
list is non-empty. Any difference then errors. An empty nav list means "not supplied" and the FK
list wins.

This does not disturb the empty-list behavior pinned by
`ValidateAndNormalizeRelations_ManyToMany_EmptyNavList_ClearsForeignKeyList`: that test covers FK
**absent** plus `tags: []` → `TagIds: []`, a different branch, unchanged here.

Comparison is by **set**, not sequence. `GetMany` does preserve request key order
(`ObjectRetrievalGrpcService.cs:111`), so a sequence comparison would work today — but that ordering
is incidental to how the retrieval loop happens to be written, and relation membership is a set
concept. Duplicate keys within a list collapse under set comparison; this edge case is deliberately
not handled.

**`OneToMany`**: the FK lives on the related entity, so no local FK exists and no conflict is
possible. Strip only, subject to the same name guard.

Conflict errors are recorded through the existing `errors` list, so they join any other relation
errors in one `InvalidArgument` rather than short-circuiting — consistent with how this validator
already reports.

## Verified assumptions

Verified against the codebase before this spec was written. Two failed and changed the design.

| # | Assumption | Result |
|---|---|---|
| A1 | The top-level loop walks every declared relation, so a post-switch strip covers all 4 kinds | ✅ `RelationValidator.cs:18-40` |
| A2 | `Candidates` is public; `SetField` exists; no `RemoveField` yet | ✅ `StructFieldAccess.cs:10,52` |
| A3 | `ValidateSingleRelation` returns at the FK check, so it needs restructuring | ✅ `RelationValidator.cs:60-65` |
| A4 | `ValidateCollectionRelation` returns at the FK-list check | ✅ `RelationValidator.cs:91-100` |
| A5 | `ValidateNestedObject` returns the resolved key as `string?`, reusable for comparison | ✅ `RelationValidator.cs:138` |
| A6 | All payload write paths route through the validator | ✅ exactly 4; `EnrichmentConsumer` writes columns directly via `UpdateColumnsAsync` and carries no client payload |
| A7 | No relation `PropertyName` ever equals its `ForeignKey` | ❌ **FAILED** — see below |
| A8 | No consumer outside the 3 stores reads nav keys from the event payload | ✅ `EnrichmentConsumer` re-reads from Postgres, already stripped |
| A9 | Existing tests assert nav retention and will need updating | ✅ none do; `EntityRelationResolverTests.cs:55` asserts `Author` on the **read** path, unaffected |
| A10 | The read path re-fetches related entities and never reads a stored nav property | ✅ `GraphAssembler.AssembleSingle` fetches by FK |
| A11 | `GetMany` ordering is not guaranteed, justifying order-insensitive comparison | ❌ **FAILED** — see below |
| A12 | Authorization runs before validation and never re-adds relation names after | ✅ `RejectDisallowedFields` throws before the validator |
| A13 | FK values, nested keys, and FK-list elements are all `StringValue` | ✅ |
| A14 | Write RPCs do not echo the request payload back | ✅ `PersistResponse` carries `success`/`key`/`trace_id`/`error` only |
| A15 | The 4 relation kinds are the complete set | ✅ switch has a throwing `default` |

### A7 failed — `PropertyName` can equal `ForeignKey`

Three clients derive the property name from the field name while *inferring* the FK as
`{RelatedType}Ids`:

- Python — `property_name=_to_pascal_case(rel["field"])`, `f"{related}Ids"` (`core.py:227,105`)
- TypeScript — `propertyName: toPascalCase(rel.field)`, `` `${relatedType}Ids` `` (`core.ts:287,98`)
- Java — `setPropertyName(toPascalCase(field.getName()))`, `relatedTypeName + "Ids"`
  (`SchemaRegistrar.java:281,336`)

A many-to-many field named `tag_ids` / `tagIds` against type `Tag` yields
`PropertyName == ForeignKey == "TagIds"`. A blind strip would delete the foreign key the validator
had just validated, turning a working write into silent relation loss.

.NET is safe structurally — a CLR type cannot have two properties with one name, and
`SchemaRegistrar.cs:71` excludes nav properties from the column list. Go is safe for a different
reason: it strips relation fields client-side at `coordinator.go:446` and never sends them.

**Effect on the design:** added the case-insensitive `PropertyName != ForeignKey` guard.

### A11 failed — and it reshaped conflict detection for collections

The original justification for order-insensitive comparison was that stream arrival order is not
guaranteed. That is wrong: `GetMany` iterates the request keys in order
(`ObjectRetrievalGrpcService.cs:111`).

Checking it surfaced something more consequential. The hydrated nav collection can legitimately be a
**strict subset** of the FK list: `GraphAssembler` skips every `Found = false` response
(`GraphAssembler.cs:116`), and the server returns `Found = false` for rows that are missing *or*
filtered out by owner/tenant mismatch. A caller reading a `Post` whose tags include one deleted or
unreadable tag receives `TagIds: [a,b,c]` alongside `Tags: [a,c]`.

**Effect on the design:** strict set equality would reject that unmodified round-trip. Conflict
detection for collections is gated on a non-empty nav list, and set comparison is retained for a
different reason than originally stated — that relation membership is a set concept, not that
ordering is unavailable.

## Known issues / accepted as out of scope

- **A partial nav subset against a present FK list errors, including when the subset arose from a
  deleted or unreadable referenced row.** Ben chose this option (2026-08-06) after the false-error
  risk was stated explicitly, preferring detection of removals over tolerance of read-time
  filtering. Callers hitting it must either send the FK list alone or re-hydrate before writing.
- **A stale hydrated nav object naming a different entity, previously ignored, now errors.** Intended
  tightening; a behavior change for existing callers, not just a cleanup.
- **Go many-to-many ids are never sent at all.** `coordinator.go:446` skips relation fields, and Go
  declares the id array *as* the relation field, so the ids reach neither the nav property nor the
  FK column. This predates and is independent of this design. Worth its own investigation.

## Out of scope

- Client-side stripping in any of the five clients. Server-side stripping fixes correctness and
  event content; it does not reclaim the upload bandwidth, which would require touching all five.
- Reconciling the fast-path and reconciliation publishers. Stripping makes their bodies agree in
  shape for relation keys, which was the observed divergence; making the event authoritative in
  general is a separate question.
- `StructConverter` still emits nulls.
- Cascade-inserting new related entities. Keyless embedded objects remain an explicit error.
