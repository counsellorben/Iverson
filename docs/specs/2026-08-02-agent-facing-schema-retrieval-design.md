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
    repeated SchemaEnrichmentKind enrichment = 12;
}

message SchemaRelation {
    string       property_name = 1;
    RelationKind kind          = 2;
    string       related_type  = 3;
    string       foreign_key   = 4;
}

enum SchemaEnrichmentKind {
    // Required zero value (proto3); never emitted — an empty `enrichment` list is "none".
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
would be pleasingly symmetric, but it hands agents registration-only concepts and exposes
`AuthorizationRules`.

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
3. Candidate fields = `KeyColumn` + `ScalarColumns`. **`FkColumns` is deliberately not added:**
   every FK property is already a scalar column, and only the `ColumnDescriptor` form carries the
   `SqlType` and `IsNullable` the projection needs. Adding it would emit each foreign key twice,
   the second time without a derivable `clr_type`, `is_array` or `is_nullable`. When
   `decision.AllowedFields` is non-null, intersect with it.
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

`is_array` is derived as `sqlType.EndsWith("[]")`. `ArrayTypeOverrides` (`SchemaBuilder.cs:249-265`)
is total over `ClrType`, every entry ends in `[]`, and no scalar SQL type does.

**`search_key_order` is the rank, not the declared value.** `SchemaBuilder.cs:115` sorts by the
declared `SearchKeyOrder` and `:169` flattens the result to names, so the declared number is never
persisted — only the resulting sequence. A type declaring orders `0` and `5` reports `0` and `1`.
This is the correct answer for a query builder, since rank *is* the sort priority, but it is not
round-trip fidelity and the field must not be read as such.

**One addition to `SchemaBuilder`:** a `SqlType → ClrType` inverse. The existing `SqlTypeMap`
(`SchemaBuilder.cs:262-265`) is keyed by SQL type but its values are `ClrTypeMapping`, which does
not carry the `ClrType` key, so it cannot answer this. The new dictionary is built from the same
`ScalarTypeMap` + `ArrayTypeOverrides` source at static-init time, so it cannot drift from
`ClrTypeToSql`. The mapping is well-defined: the nine scalar and nine array SQL types are
pairwise distinct.

**No audit logging.** `AuditLog` records data access; a schema-shape read is not that, and a
per-type denial entry on every catalog call would be noise.

### 3. Client surface

**One method per language, on the data-plane client** — not on `SchemaRegistrar`. The registrar is
a registration-time path used with schema-admin credentials; `GetSchema` is a data-plane read whose
entire response depends on the acting user.

| Client | Type | Signature |
|---|---|---|
| .NET | `SchemaCatalogClient` (new) | `Task<IReadOnlyList<SchemaType>> GetSchemaAsync(string traceId = "", CancellationToken ct = default)` |
| Java | `IversonClient` | `List<SchemaType> getSchema(String traceId, String actingUserToken)` (corrected 2026-08-03 — see A21) |
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

**.NET and Java both require the acting user per call.** (Corrected 2026-08-03 — see A21. The
original text said "the other four clients"; Java in fact holds only its *service* credential on the
client instance, and its acting user travels on a per-call `CallOptions.Key`. Java's `getSchema`
therefore takes a trailing `String actingUserToken`, exactly as every `EntityCoordinator` data-plane
method does.) TypeScript, Python and Go hold identity on the client instance or the call context, so
a signature without an identity parameter still carries one; .NET
holds it in neither place today — grepping the .NET client for `ActingUser` returns hits only inside
`ActingUserMetadata.cs`, a per-call `Metadata` extension. `SchemaCatalogClient` therefore takes an
acting-user token provider as a constructor dependency and applies `WithActingUser` itself on every
call, so the signature stays entity- and identity-free. `AddIversonClient` gains a
`Func<Task<string>>? actingUserTokenProvider = null` parameter alongside the existing
`dataPlaneTokenProvider`, declared before the trailing `params Assembly[]`.

*Release note (added 2026-08-03, final whole-branch review, Minor 9):* inserting
`actingUserTokenProvider` before the trailing `params Assembly[] entityAssemblies` is a
**source-breaking** change for any external caller that passes assemblies positionally after three
arguments. Both in-repo callers (`Iverson.Client.Sample/Program.cs`, `Iverson.LoadTest/Program.cs`)
pass `entityAssemblies:` by name and are unaffected. Compile-time only — no runtime behaviour change.

**An empty catalog is an authorization outcome, not an empty registry.** `RowFieldAuthorizationEvaluator`
is fail-closed on every axis, and §2's step 2 omits every `Denied` type. An absent acting user is only
one of the causes; the others produce exactly the same empty response:

- no acting user on the call (`RowFieldAuthorizationEvaluator.cs:14-15`) — e.g. no token provider configured;
- an acting user with no `tenant_id` claim (`:20-22`);
- registered types that declare no `Authorization` rules at all (`:10-12`);
- registered types that declare no tenant field, so `TenantColumn` is null/empty (`:18-19`).

All four make a type unreadable through *every* RPC, not just `GetSchema` — the catalog lists
precisely the types the caller can actually query, which is the intended property. Client
documentation must name all four causes, not just the missing acting user, or an operator debugging
an empty catalog hunts the wrong thing.

Note also that `SchemaCatalogClient` and `SchemaRegistrar` share one DI-registered
`ObjectMappingServiceClient`, credentialed with the client-credentials token — the registrar
separation above is a type-level distinction, not a credential-level one.

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
4. A type where filtering leaves nothing readable is omitted. Note (2026-08-03): the production
   `RowFieldAuthorizationEvaluator` unconditionally re-admits the key column
   (`RowFieldAuthorizationEvaluator.cs:65`), so it can never itself produce an empty `AllowedFields`.
   The guard in `GetSchema` is therefore fail-closed defence-in-depth, and the test drives it with a
   substituted `IRowFieldAuthorizationEvaluator` returning an empty `AllowedFields` rather than with
   real authorization rules.
5. A relation whose `related_type` was omitted is dropped from the surviving type.
6. **Added 2026-08-03 (final whole-branch review, Important 2).** A relation whose `foreign_key`
   column was removed by a `FieldPermission` is dropped too. Every FK property is also an ordinary
   scalar column (`SchemaBuilder.cs:53-57` adds every non-key property to `scalars`; `:107-112`
   *additionally* records FK-named ones in `fks`), so filtering relations only on the survival of
   the related type would still disclose the excluded column's exact name as `relation.foreign_key`.
   Relations are therefore filtered on **both** `survivingNames.Contains(RelatedTypeName)` and
   `decision.AllowedFields is null || decision.AllowedFields.Contains(ForeignKey)`.
7. **Added 2026-08-03 (final whole-branch review, Minor 7).** A column whose persisted SQL type is
   not in this build's map is skipped and logged, not fatal. `SchemaRegistry.LoadAsync` rehydrates
   descriptors written by older builds, so `GetSchema` uses the non-throwing `TrySqlTypeToClr`;
   losing one legacy column must not take discovery down for every type. The write path
   (`ClrTypeToSql`) still throws, where failing registration is correct.

**Type recovery** — table-driven over **`Enum.GetValues<ClrType>()`**, asserting that for every
`ClrType`, `SqlTypeToClr(ClrTypeToSql(t, isArray: false))` returns `t`, and that
`SqlTypeToClr(ClrTypeToSql(t, isArray: true))` returns `(t, is_array: true)`.

Iterating the enum rather than the map is deliberate on two counts. `ScalarTypeMap` and
`ArrayTypeOverrides` are `private static readonly` (`SchemaBuilder.cs:233`, `:250`), so tests
cannot enumerate them even with `InternalsVisibleTo` — only the `internal static` methods
`ClrTypeToSql` (`:267`) and the new inverse are reachable. And enumerating the enum is the
stronger check anyway: a newly added `ClrType` with no `ScalarTypeMap` entry fails here (via
`ClrTypeToSql`'s `ArgumentOutOfRangeException` at `:274-275`), whereas iterating the map would
silently skip it.

**Flag composition** — a field declared both `metadata` and `search_key` reports both, **and a field
declared both `@IversonSummary` and `@IversonKeywords` reports both enrichment kinds.** The proto's
booleans are independent and all five clients now enforce that they compose; the projection must
not reintroduce exclusivity.

**Clients** — one test per language: the method issues `GetSchema` against a mock stub and surfaces
the returned types, following each suite's existing mock-stub pattern rather than a new harness. For
.NET, additionally: a client constructed with an acting-user token provider reaches the stub
carrying the `x-acting-user-authorization` metadata key.

## Out of scope

The MCP server (`docs/specs/2026-07-22-mcp-server-design.md`) — it is a separate, already-specified
consumer of this RPC and is not absorbed here. Any richer query surface beyond the six search RPCs
that already exist. A single-type request filter. Persisting `is_array` or the declared
`SearchKeyOrder` value to improve response fidelity.

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
| A20 | **`ScalarColumns` and `FkColumns` overlap — every FK column is also a scalar column** | `SchemaBuilder.cs:56-57` adds every non-key property to `scalars` unconditionally; `:106-113` appends the same property to `fks` later in the *same* loop iteration with no `continue`. Corroborated by `ToTableSchema` (`:181-184`), which builds the physical table from `KeyColumn` + `ScalarColumns` only — FK columns are not separate columns because they are already scalars |
| A10 | `ScalarTypeMap` is injective on SQL type | `SchemaBuilder.cs:233-245` — nine entries, SQL types `UUID`, `TEXT`, `INTEGER`, `BIGINT`, `REAL`, `DOUBLE PRECISION`, `BOOLEAN`, `TIMESTAMPTZ`, `BYTEA`, all distinct |
| A11 | **Corrected — was two array types, now nine.** Every array SQL type ends in `[]`; no scalar SQL type does | `SchemaBuilder.cs:249-265` — `ArrayTypeOverrides` is total over `ClrType`: `UUID[]`, `TEXT[]`, `INTEGER[]`, `BIGINT[]`, `REAL[]`, `DOUBLE PRECISION[]`, `BOOLEAN[]`, `TIMESTAMPTZ[]`, `BYTEA[]`. The 18 SQL strings are pairwise distinct, so the `SqlType → ClrType` inverse stays well-defined. Restated after the `array-column-mapping` branch merged |
| A12 | **Corrected.** `SearchKeyColumns` is ordered, but by rank — the declared value is not persisted | `SchemaBuilder.cs:115` sorts by declared order; `:169` flattens to `ConvertAll(sk => sk.Name)`. `search_key_order` is therefore the rank |
| A13 | `RelationDescriptor` and `EnrichmentKind` shapes | `SchemaDescriptor.cs:61-65` `(PropertyName, Kind, RelatedTypeName, ForeignKey)`; `:47` `enum EnrichmentKind { Summary, Keywords, Extracted }` |
| A14 | **Failed for .NET.** Four clients have a non-generic data-plane client holding a mapping stub; .NET does not | TypeScript `core.ts:518-524` (`_mappingClient`, `_actingUserToken`); Java `IversonClient.java:31` (`mappingStub`); Python `core.py:506,53` (`IversonClient`, `self._mapping_stub`, `acting_user_token`); Go `coordinator.go:61-70` (`MappingStub`). `grep -rn 'class IversonClient' Iverson.Clients/DotNet/` returns nothing — hence `SchemaCatalogClient` |
| A15 | Each client has a reusable acting-user call path | TypeScript `callUnary` (`core.ts:139`); Python `_ActingUserAuthPlugin` + channel call-credentials (`core.py:529-538`); Go `WithActingUserToken` (`auth.go:23`); Java `CallOptions` (`OAuth2ClientCredentials.java:56-58`); .NET `ActingUserMetadata.WithActingUser` (`ActingUserMetadata.cs:9`) |
| A21 | **CORRECTED 2026-08-03 (final whole-branch review, Critical 1). Only three clients bind the acting user ambiently; Java and .NET both require it per call.** Three clients bind the acting user to the client instance or call context: TypeScript resolves an instance-level `actingUserToken` inside `callUnary` (`core.ts:122-139`); Python installs `acting_user_token` on the channel at construction (`core.py:553-564`); Go takes `ctx`, where `WithActingUserToken` puts it. **Java does not.** The original claim — that Java builds `mappingStub` with `.withCallCredentials(credentials)` and therefore carries an identity — conflated two distinct credentials. Those `CallCredentials` are the service's own OAuth2 *client-credentials* token; the acting user travels separately, on a per-call `CallOptions.Key` (`OAuth2ClientCredentials.java:35-36` `ACTING_USER_TOKEN`; `:56-58` reads it off `requestInfo.getCallOptions()` and only then emits `x-acting-user-authorization`). Every Java data-plane method accordingly takes a trailing `String actingUserToken` and applies `stub.withOption(ACTING_USER_TOKEN, token)` (`EntityCoordinator.java:269-273`). For .NET, `grep -rn "ActingUser" Iverson.Clients/DotNet/Iverson.Client.Core/*.cs` returns hits only inside `ActingUserMetadata.cs` — a per-call `Metadata` extension. **Consequence:** a `getSchema(String traceId)` signature carries an identity in TypeScript, Python and Go, but in Java and .NET it carries none, and the server returns an empty catalog on every call. Java's `getSchema` therefore takes `(String traceId, String actingUserToken)`, matching `EntityCoordinator`; .NET's `SchemaCatalogClient` takes an acting-user token provider as a constructor dependency |
| A16 | Each suite has an ObjectMappingService mock-stub pattern | `SchemaRegistrarTests.cs`, `TestCoordinatorFactory.cs`, `SchemaRegistrarTest.java`, `schema-registrar.test.ts`, `test_auth.py`, `registrar_test.go` |
| A17 | Generated proto types are already public/exported per client | Every client's `SchemaRegistrar` already builds and passes `SchemaRequest`/`TypeDescriptor` across its public API |
| A18 | **Corrected.** Server tests reach the type mapping through methods, not the maps | `Iverson.Api.csproj:10-13` declares two `InternalsVisibleTo` attributes, but `ScalarTypeMap` (`SchemaBuilder.cs:233`) and `ArrayTypeOverrides` (`:250`) are `private static readonly` and therefore unreachable from tests. `ClrTypeToSql` (`:267`) is `internal static`, so the round-trip test drives the enum through the two methods instead — see §4 |
| A19 | Nothing breaks when a second read-only consumer of `SchemaRegistry.All` is added | One existing consumer, `Program.cs:420` (`foreach (var descriptor in schemaRegistry.All.Values)`), read-only. `All` returns `IReadOnlyDictionary` |
