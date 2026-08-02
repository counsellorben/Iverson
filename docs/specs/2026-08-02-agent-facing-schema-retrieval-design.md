# Agent-Facing Schema Retrieval (`GetSchema`) — Design

**Date:** 2026-08-02
**Status:** Approved, awaiting critical design review
**Scope:** one new RPC on `ObjectMappingService`, its server implementation, and a client method in all five languages. No change to any existing RPC, no change to the persisted schema model, no new storage.

## Context

Part 5 — the last — of the five-part metadata/tensor-search initiative. Parts 1–4 are complete
and merged: metadata foundation, Ollama ingest enrichment, tensor re-ranking/fusion, and derived
vector signals.

Part 1 deferred this piece by name. `docs/specs/2026-07-26-metadata-foundation-design.md:135-142`:

> No schema-read RPC exists today (`RegisterSchema` is the only schema RPC — verified).
> […] A client-facing `GetSchema`-style surface is deliberately deferred to the agent-facing
> sub-project (#5), which is the piece that needs it. Decision made by Ben 2026-07-26.

## Problem

An agent that wants to query Iverson has no way to learn what is queryable.

`RegisterSchema` is still the only schema RPC (verified: `object_mapping.proto:10-14`). The
registry holds type descriptions, field descriptions, metadata flags, search keys, chunk and
embedding fields, relations and enrichment targets — all of it readable in-process by server-side
consumers, none of it reachable by a client. So an agent cannot discover what types exist, what
their fields mean, which fields are filterable, which are orderable, or which can be searched
semantically. It can only query a schema that was compiled into it.

`docs/specs/2026-07-22-mcp-server-design.md` works around exactly this: its `entities.ts` loads
"the deployer's `@IversonEntity` classes", so the MCP server knows the schema because a human
put it there. That is the workaround this design removes.

The query surface itself is not the gap. Search, GroupBy, Aggregate, Pipeline, SearchSimilar and
SearchChunks all exist. Discovery is what is missing.

## Goal

One RPC returning the catalog of registered types the calling identity may read, with fields
filtered by the same field-level authorization that governs every data read, plus a client method
in all five languages.

## Design

### 1. Proto surface

One RPC on the existing `ObjectMappingService`, beside `RegisterSchema`:

```proto
rpc GetSchema (GetSchemaRequest) returns (GetSchemaResponse);

message GetSchemaRequest  { string trace_id = 1; }
message GetSchemaResponse { repeated SchemaType types = 1; }

message SchemaType {
    string                  name        = 1;
    string                  description = 2;
    repeated SchemaField    fields      = 3;
    repeated SchemaRelation relations   = 4;
}

message SchemaField {
    string  name             = 1;
    string  description      = 2;
    ClrType clr_type         = 3;
    bool    is_array         = 4;
    bool    is_key           = 5;
    bool    is_nullable      = 6;
    bool    is_metadata      = 7;
    bool    is_search_key    = 8;
    int32   search_key_order = 9;
    bool    is_embedding     = 10;
    bool    is_chunk         = 11;
    SchemaEnrichmentKind enrichment = 12;
}

message SchemaRelation {
    string       property_name = 1;
    RelationKind kind          = 2;
    string       related_type  = 3;
    string       foreign_key   = 4;
}

enum SchemaEnrichmentKind {
    ENRICHMENT_NONE      = 0;
    ENRICHMENT_SUMMARY   = 1;
    ENRICHMENT_KEYWORDS  = 2;
    ENRICHMENT_EXTRACTED = 3;
}
```

**The request takes no selector.** Discovery's first question is "what is here?", and a caller
cannot name a type it has not heard of. A single-type filter is deliberately not offered: nothing
has asked for it, and it is additive later.

**A purpose-built response, not the registration `TypeDescriptor`.** Round-tripping `TypeDescriptor`
would be pleasingly symmetric but is **lossy**: `is_array` is never persisted (`SchemaBuilder.cs:55`
consumes it to pick a SQL type and discards it), and only `Guid[]` and `float[]` receive a distinct
SQL type. A declared `string[]` persists as `TEXT`, indistinguishable from `string`, so its
`is_array` could not be reconstructed without adding stored state to serve a read path. Reusing
`TypeDescriptor` would also hand agents registration-only concepts and expose `AuthorizationRules`.

`ClrType` and `RelationKind` are reused rather than redefined — both already live in this proto
(`:31` and `:24`).

**Every field answers "can the agent build a valid query?"** — `clr_type` and `is_array` decide
which operators are legal, `is_metadata` what is filterable on chunk points, `is_search_key` what
is orderable, `is_embedding`/`is_chunk` what `SearchSimilar`/`SearchChunks` can target, `is_key`
what `Get` takes, `relations` what can be joined.

**Deliberately excluded:** `TableName`, `CollectionName`, SQL type strings, `LargeFieldColumns`
(a materialized-view performance detail, not query semantics), the tenant column (the server
enforces it; an agent filtering on it is a bug), and `AuthorizationRules` — which would tell a
caller precisely what it is not allowed to see.

### 2. Server

**Placement and authorization.** A `GetSchema` override on `ObjectMappingGrpcService`, beside
`RegisterSchema`, carrying **no** policy attribute. It therefore inherits the ambient requirement:
`Program.cs:143-145` sets `FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()`,
and `Program.cs:426` maps the service with no `RequireAuthorization` override. The RPC is
authenticated but not `SchemaAdmin` — agents authenticate as ordinary end users, and gating this
behind `SchemaAdmin` (as `RegisterSchema` is, at `ObjectMappingGrpcService.cs:36`) would put
discovery out of reach of the callers it exists to serve.

**Algorithm.** For each `SchemaDescriptor` in `_registry.All`:

1. `decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read)`
2. `decision.Denied` → omit the type entirely.
3. Candidate fields = `KeyColumn` + `ScalarColumns` + `FkColumns`. When `decision.AllowedFields`
   is non-null, intersect with it.
4. Empty field set → omit the type. A type with nothing readable is a dead end, and listing its
   name is itself a disclosure.
5. Project the survivors.

`OwnershipRequired` is ignored here by design: it constrains which **rows** a caller sees, not
which fields exist. A caller restricted to its own rows queries against the same schema.

**Cross-type consistency.** A relation whose `related_type` was omitted from the response is itself
omitted, so the catalog never invites a join to a type the caller cannot see.

**Projection.** `is_key` from `KeyColumn.Name`; `is_nullable` from `ColumnDescriptor.IsNullable`;
`is_metadata` from `MetadataColumns`; `is_embedding`/`is_chunk` from `VectorFields`/`ChunkFields`;
`description` from `FieldDescriptions`; `enrichment` from `EnrichmentTargets`; relations from
`Relations` (`PropertyName`, `Kind`, `RelatedTypeName`, `ForeignKey`).

`is_array` is derived as `sqlType.EndsWith("[]")`. This is complete for everything the storage
layer treats as an array: `ArrayTypeOverrides` (`SchemaBuilder.cs:250-255`) contains exactly
`ClrGuid → "UUID[]"` and `ClrFloat → "REAL[]"`, and no scalar SQL type ends in `[]`.

**`search_key_order` is the rank, not the declared value.** `SchemaBuilder.cs:115` sorts by the
declared `SearchKeyOrder` and `:169` flattens the result to names, so the declared number is never
persisted — only the resulting sequence. A type declaring orders `0` and `5` reports `0` and `1`.
This is the correct answer for a query builder, since rank *is* the sort priority, but it is not
round-trip fidelity and the field must not be read as such.

**One addition to `SchemaBuilder`:** a `SqlType → ClrType` inverse. The existing `SqlTypeMap`
(`SchemaBuilder.cs:262-265`) is keyed by SQL type but its values are `ClrTypeMapping`, which does
not carry the `ClrType` key, so it cannot answer this. The new dictionary is built from the same
`ScalarTypeMap` + `ArrayTypeOverrides` source at static-init time, so it cannot drift from
`ClrTypeToSql`. The mapping is well-defined: all nine scalar SQL types are distinct, and both
array overrides are distinct from every scalar.

**No audit logging.** `AuditLog` records data access; a schema-shape read is not that, and a
per-type denial entry on every catalog call would be noise.

### 3. Client surface

**One method per language, on the data-plane client** — not on `SchemaRegistrar`. The registrar is
a registration-time path used with schema-admin credentials; `GetSchema` is a data-plane read whose
entire response depends on the acting user.

| Client | Type | Signature |
|---|---|---|
| .NET | `SchemaCatalogClient` (new) | `Task<IReadOnlyList<SchemaType>> GetSchemaAsync(string traceId = "", CancellationToken ct = default)` |
| Java | `IversonClient` | `List<SchemaType> getSchema(String traceId)` |
| TypeScript | `IversonClient` | `async getSchema(traceId = ''): Promise<SchemaType[]>` |
| Python | `IversonClient` | `def get_schema(self, trace_id: str = "") -> list[SchemaType]` |
| Go | `IversonClient` | `func (c *IversonClient) GetSchema(ctx context.Context, traceID string) ([]*pb.SchemaType, error)` |

**.NET needs a new type; the other four do not.** Java, TypeScript, Python and Go each have a
non-generic `IversonClient` already holding a mapping stub and an acting-user mechanism. .NET has
no `IversonClient` at all — its data-plane type is `EntityCoordinator<T>`, generic over an entity
and registered `AddTransient(typeof(EntityCoordinator<>))` (`ServiceCollectionExtensions.cs:74`).
Putting an entity-independent catalog call on it would force callers to name an arbitrary `T` to
ask what types exist. `SchemaCatalogClient` is a small non-generic type taking the mapping client,
registered `AddSingleton` exactly as `SchemaRegistrar` is (`:76`). Decision made by Ben 2026-08-02.

**Returns the generated proto type**, unwrapped to the `types` list. This matches the search family
— TypeScript's `searchChunks()` returns `ChunkSearchResponse[]` and `aggregate()` returns
`AggregateResponse`, both generated types. A hand-rolled idiomatic model in five languages would be
five times the surface and five places to drift from the proto.

Each implementation reuses its client's existing call path, so no new credential or acting-user
plumbing appears anywhere: TypeScript `callUnary`, Python channel call-credentials plus
`_ActingUserAuthPlugin`, Go `WithActingUserToken(ctx, …)`, Java `CallOptions`, .NET
`Metadata.WithActingUser(token)`.

### 4. Testing

**Server — projection and filtering, both directions:**

1. An unrestricted caller receives every registered type with every field.
2. A denied type is **omitted**, not returned empty — asserted on absence of the name, since
   returning the name is the disclosure.
3. A caller with a restricted `AllowedFields` sees only those fields, **and** the excluded field's
   `description` appears nowhere in the response. This is the specific leak §2 exists to prevent,
   so it is asserted separately rather than riding on the field-list check.
4. A type where filtering leaves nothing readable is omitted.
5. A relation whose `related_type` was omitted is dropped from the surviving type.

**Type recovery** — table-driven over **`Enum.GetValues<ClrType>()`**, asserting that for every
`ClrType`, `SqlTypeToClr(ClrTypeToSql(t, isArray: false))` returns `t`, plus the two array cases
round-tripping to `(ClrGuid, is_array)` and `(ClrFloat, is_array)`.

Iterating the enum rather than the map is deliberate on two counts. `ScalarTypeMap` and
`ArrayTypeOverrides` are `private static readonly` (`SchemaBuilder.cs:233`, `:250`), so tests
cannot enumerate them even with `InternalsVisibleTo` — only the `internal static` methods
`ClrTypeToSql` (`:267`) and the new inverse are reachable. And enumerating the enum is the
stronger check anyway: a newly added `ClrType` with no `ScalarTypeMap` entry fails here (via
`ClrTypeToSql`'s `ArgumentOutOfRangeException` at `:274-275`), whereas iterating the map would
silently skip it.

**Flag composition** — a field declared both `metadata` and `search_key` reports both. The proto's
booleans are independent and all five clients now enforce that they compose; the projection must
not reintroduce exclusivity.

**Clients** — one test per language: the method issues `GetSchema` against a mock stub and surfaces
the returned types, following each suite's existing mock-stub pattern rather than a new harness.

## Out of scope

The MCP server (`docs/specs/2026-07-22-mcp-server-design.md`) — it is a separate, already-specified
consumer of this RPC and is not absorbed here. Any richer query surface beyond the six search RPCs
that already exist. A single-type request filter. Persisting `is_array` or the declared
`SearchKeyOrder` value to improve response fidelity.

## Known issues — pre-existing, not addressed here

A declared `string[]` (or any array other than `Guid[]`/`float[]`) maps to its scalar SQL type at
registration — `ClrTypeToSql` (`SchemaBuilder.cs:267-276`) consults `ArrayTypeOverrides` first and
falls through to `ScalarTypeMap` for every other type, so a `string[]` is persisted as `TEXT`. This
design reports `is_array = false` for such a field, which is consistent with how the server stores
and queries it, but it means the response reflects storage rather than the original declaration.
Whether that mapping is itself correct is a pre-existing question about the write path and is not
touched here.

## Verified assumptions

Verified against `main@5884b07`.

| # | Assumption | Evidence |
|---|---|---|
| A1 | `object_mapping.proto` is the sole definition of `ObjectMappingService` | `grep -rln 'service ObjectMappingService' --include=*.proto` returns exactly `Iverson.Clients/Common/Proto/object_mapping.proto` |
| A2 | `ClrType` and `RelationKind` exist in that proto and are reusable | `object_mapping.proto:31` and `:24` |
| A3 | `trace_id` on request messages is the established convention | 7 occurrences in `object_mapping.proto` |
| A4 | The six new message/enum names are free | `grep -nE '^message \|^enum '` — existing names are `RelationKind`, `ClrType`, `PropertyDescriptor`, `RelationDescriptor`, `RowPermission`, `FieldPermission`, `AuthorizationRules`, `TypeDescriptor`, `SchemaRequest`, `SchemaResponse`, `MappingGetRequest`, `MappingWriteRequest`, `MappingDeleteRequest`, `MappingResponse`, `MappingDeleteResponse`. No collision |
| A5 | `SchemaRegistry.All` exposes every registered descriptor | `SchemaRegistry.cs:13` — `public IReadOnlyDictionary<string, SchemaDescriptor> All => _schemas` |
| A6 | `AuthorizationAction.Read` exists and `Evaluate` takes `(schema, actingUser, action)` | `IRowFieldAuthorizationEvaluator.cs:6` and `:10-13` |
| A7 | `AllowedFields` spans key + scalars + FKs + vector/chunk source names | `IRowFieldAuthorizationEvaluator.cs:21-25` doc comment, verbatim |
| A8 | The service requires authentication ambiently | `Program.cs:143-145` `FallbackPolicy = RequireAuthenticatedUser()`; `Program.cs:426` maps the service with no `RequireAuthorization` override |
| A9 | `SchemaDescriptor` carries every member the projection reads | `SchemaDescriptor.cs:3-45` — `KeyColumn`, `ScalarColumns`, `FkColumns`, `VectorFields`, `ChunkFields`, `Relations`, `SearchKeyColumns`, `MetadataColumns`, `Description`, `FieldDescriptions`, `EnrichmentTargets` all present |
| A10 | `ScalarTypeMap` is injective on SQL type | `SchemaBuilder.cs:233-245` — nine entries, SQL types `UUID`, `TEXT`, `INTEGER`, `BIGINT`, `REAL`, `DOUBLE PRECISION`, `BOOLEAN`, `TIMESTAMPTZ`, `BYTEA`, all distinct |
| A11 | Only `UUID[]` and `REAL[]` end in `[]` | `SchemaBuilder.cs:250-255` — `ArrayTypeOverrides` has exactly those two; no scalar SQL type ends in `[]` |
| A12 | **Corrected.** `SearchKeyColumns` is ordered, but by rank — the declared value is not persisted | `SchemaBuilder.cs:115` sorts by declared order; `:169` flattens to `ConvertAll(sk => sk.Name)`. `search_key_order` is therefore the rank |
| A13 | `RelationDescriptor` and `EnrichmentKind` shapes | `SchemaDescriptor.cs:61-65` `(PropertyName, Kind, RelatedTypeName, ForeignKey)`; `:47` `enum EnrichmentKind { Summary, Keywords, Extracted }` |
| A14 | **Failed for .NET.** Four clients have a non-generic data-plane client holding a mapping stub; .NET does not | TypeScript `core.ts:518-524` (`_mappingClient`, `_actingUserToken`); Java `IversonClient.java:31` (`mappingStub`); Python `core.py:506,53` (`IversonClient`, `self._mapping_stub`, `acting_user_token`); Go `coordinator.go:61-70` (`MappingStub`). `grep -rn 'class IversonClient' Iverson.Clients/DotNet/` returns nothing — hence `SchemaCatalogClient` |
| A15 | Each client has a reusable acting-user call path | TypeScript `callUnary` (`core.ts:139`); Python `_ActingUserAuthPlugin` + channel call-credentials (`core.py:529-538`); Go `WithActingUserToken` (`auth.go:23`); Java `CallOptions` (`OAuth2ClientCredentials.java:56-58`); .NET `ActingUserMetadata.WithActingUser` (`ActingUserMetadata.cs:9`) |
| A16 | Each suite has an ObjectMappingService mock-stub pattern | `SchemaRegistrarTests.cs`, `TestCoordinatorFactory.cs`, `SchemaRegistrarTest.java`, `schema-registrar.test.ts`, `test_auth.py`, `registrar_test.go` |
| A17 | Generated proto types are already public/exported per client | Every client's `SchemaRegistrar` already builds and passes `SchemaRequest`/`TypeDescriptor` across its public API |
| A18 | **Corrected.** Server tests reach the type mapping through methods, not the maps | `Iverson.Api.csproj:10-13` declares two `InternalsVisibleTo` attributes, but `ScalarTypeMap` (`SchemaBuilder.cs:233`) and `ArrayTypeOverrides` (`:250`) are `private static readonly` and therefore unreachable from tests. `ClrTypeToSql` (`:267`) is `internal static`, so the round-trip test drives the enum through the two methods instead — see §4 |
| A19 | Nothing breaks when a second read-only consumer of `SchemaRegistry.All` is added | One existing consumer, `Program.cs:420` (`foreach (var descriptor in schemaRegistry.All.Values)`), read-only. `All` returns `IReadOnlyDictionary` |
