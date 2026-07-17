# Design: SOLID Remediation for ObjectSearchGrpcService & ObjectMappingGrpcService

## Origin

A `system-architectural-review` flagged both classes as god-service/responsibility-concentration risks. A follow-on `solid-design-review` produced 5 findings with concrete remediations; this spec covers the 4 substantive ones (Finding #5, an optional enum-switch cleanup, is explicitly out of scope — cosmetic, no defect history, can be a separate tiny follow-up anytime).

## Scope

Four extractions, all additive (no behavior change):

1. `QdrantFilterBuilder.ApplyOwnership` — closes the Qdrant authorization-filter duplication (Finding #1)
2. `OutboxPublisher` — closes the outbox-publish-block duplication (Finding #3), across **5** call sites
3. `EntityRelationResolver` — extracts relation-graph resolution out of `ObjectMappingGrpcService` (Finding #2 + #4)
4. `SchemaRegistrationOrchestrator` — extracts schema/DDL registration out of `ObjectMappingGrpcService` (Finding #2)

## Section A: `QdrantFilterBuilder.ApplyOwnership`

**Problem:** `ObjectSearchGrpcService.SearchSimilar` (lines 154-158) and `SearchChunks` (lines 225-229) each hand-roll the identical ownership-filter construction directly against `Qdrant.Client.Grpc.Conditions`, rather than going through `Iverson.Vector.QdrantFilterBuilder` — which already owns every other Qdrant filter-translation concern (`Build`, `MatchParentId`) but has no ownership equivalent. This is the mechanical cause of 3 separate real regressions fixed this session (missed log-sanitization, missed field-masking in exactly these two methods).

**Verified constraint:** `Iverson.Vector` does not reference `Iverson.Api` (confirmed: `Iverson.Vector.csproj` only references `Iverson.Client.Contracts`; `Iverson.Api.csproj` references `Iverson.Vector`, not the reverse). So the new method cannot take `AuthorizationDecision`/`SchemaDescriptor` (both defined in `Iverson.Api`) — it must take primitives, the same way `Iverson.StarRocks.AuthorizationConstraint` is a neutral DTO independent of the Api-layer's own types.

```csharp
// Iverson.Vector/QdrantFilterBuilder.cs
public static Filter? ApplyOwnership(Filter? filter, bool ownershipRequired, string? ownerFieldCamelCase, string? ownerValue)
{
    if (!ownershipRequired) return filter;
    filter ??= new Filter();
    filter.Must.Add(Conditions.MatchKeyword(ownerFieldCamelCase!, ownerValue!));
    return filter;
}
```

Call sites become:
```csharp
filter = QdrantFilterBuilder.ApplyOwnership(filter, decision.OwnershipRequired, schema.Authorization?.OwnerField?.ToCamelCase(), decision.OwnerValue);
```

**Note the `?.`, not `!.`:** C# evaluates all method arguments eagerly, so `schema.Authorization!.OwnerField!.ToCamelCase()` would dereference unconditionally even when `OwnershipRequired` is false — and `Authorization`/`OwnerField` are independently nullable (a schema can have bypass `RowPermissions` with no `OwnerField` at all), so a bypass-role caller on such a schema would hit a `NullReferenceException`. `?.` lets a null flow through harmlessly as the argument value; `ApplyOwnership`'s own internal `if (!ownershipRequired) return filter;` guard means the null is never dereferenced (the evaluator guarantees `OwnershipRequired == true` implies `OwnerField` is non-empty, so `ApplyOwnership`'s existing `!` usage on that path stays correct).

No DTO type introduced — 3 scalars used at exactly 2 call sites doesn't earn one (field-masking already goes through the separate, existing `AuthorizationFieldMasking.MaskDisallowedFields`, so `ApplyOwnership` never needs `AllowedFields`).

**Tests** (add to existing `Iverson.Vector.Tests/QdrantFilterBuilderTests.cs`):
- `ownershipRequired: false` → filter passthrough unchanged (including `null` input)
- `true` + `null` filter → new filter with the `MatchKeyword` condition
- `true` + existing filter → condition appended, existing conditions preserved

## Section B: `OutboxPublisher`

**Problem:** the "produce to Kafka → delete outbox row on success → log one of two distinct failure modes" block is copy-pasted **5 times**: `ObjectMappingGrpcService.Post` (213-242), `.Update` (278-307), `.Delete` (359-388), `ObjectPersistenceGrpcService.Post` (63-90), `.Update` (135-164). (Originally scoped as 4 sites; verification found `ObjectPersistenceGrpcService.Update` has the identical duplication — confirmed via direct read, and confirmed no `Delete` method exists on that class, so 5 is the complete set.)

It also absorbs two smaller duplications discovered in the same block: the `SchemaVersion = "1"` constant (independently defined in both files) and the `traceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty` resolution (repeated at all 5 sites).

**Verified type correction:** `StoreTargeting.DetermineTargetStores` returns a single `StoreTarget` — a `[Flags]` enum where one value already represents multiple targets via bitwise OR (confirmed: `internal static StoreTarget DetermineTargetStores(SchemaDescriptor schema)` in `Iverson.Api/Schema/StoreTargeting.cs`). The publisher's parameter is `StoreTarget targetStores`, not a list.

```csharp
// Iverson.Api/Grpc/OutboxPublisher.cs
public interface IOutboxPublisher
{
    Task PublishAsync(
        EntityEventType eventType,
        string typeName,
        string key,
        string payloadJson,
        string? requestTraceId,
        StoreTarget targetStores,
        Guid outboxRowId,
        string opLabel,   // e.g. "Mapping.Post", "Persistence.Update" — the existing bracketed log prefix
        CancellationToken ct = default);
}

public sealed class OutboxPublisher(
    IEventProducer events, IOutboxWriter outboxWriter, ILogger<OutboxPublisher> logger)
    : IOutboxPublisher
{
    private const string SchemaVersion = "1";

    public async Task PublishAsync(
        EntityEventType eventType, string typeName, string key, string payloadJson,
        string? requestTraceId, StoreTarget targetStores,
        Guid outboxRowId, string opLabel, CancellationToken ct = default)
    {
        var traceId = requestTraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty;
        var published = false;
        try
        {
            await events.ProduceAsync(EntityTopics.Events, key,
                new EntityEvent(eventType, typeName, key, payloadJson, traceId, SchemaVersion, DateTimeOffset.UtcNow, targetStores));
            published = true;
            await outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId);
        }
        catch (Exception ex) when (!published)
        {
            logger.LogWarning(ex, "[{Op}] Opportunistic publish failed for type={Type} key={Key} — ReconciliationQueueWorker will retry from the durable outbox row",
                opLabel, typeName.SanitizeForLog(), key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Op}] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row",
                opLabel, typeName.SanitizeForLog(), key);
        }
    }
}
```

Both gRPC services keep their own `_outboxWriter.UpsertAndEnqueueOutboxAsync(...)` / `EnqueueDeleteOutboxRowAsync(...)` calls as-is (that part legitimately differs between create/update and delete, and stays where the transaction is scoped) — only the publish-attempt block moves out, at all 5 sites.

**Tests** (new, `Iverson.Api.Tests/Grpc/OutboxPublisherTests.cs`):
- Success → outbox row deleted, no warning logged
- `events.ProduceAsync` throws → "Opportunistic publish failed" warning, outbox row **not** deleted
- Produce succeeds but `DeleteOutboxRowIfPresentAsync` throws → "Publish succeeded but outbox cleanup failed" warning
- Trace-id resolution: `requestTraceId` present → used as-is; absent + `Activity.Current` present → its trace id; both absent → empty string

## Section C: `EntityRelationResolver`

**Problem:** `ObjectMappingGrpcService.ResolveRelationsAsync` + its 3 siblings (`ResolveSingleRelationAsync`/`ResolveManyToManyAsync`/`ResolveOneToManyAsync`) need only 4 of the class's 14 constructor dependencies, but are reachable from (and coupled to) all 14 today. Each of the 3 siblings also repeats an identical 4-line "evaluate authorization → check denied/ownership-mismatch → mask disallowed fields" block verbatim (Finding #4).

```csharp
// Iverson.Api/Grpc/EntityRelationResolver.cs
public interface IEntityRelationResolver
{
    Task ResolveRelationsAsync(Struct entityStruct, SchemaDescriptor schema, int depth, CancellationToken ct);
}

public sealed class EntityRelationResolver(
    SchemaRegistry registry,
    IEntityRepository entities,
    IRowFieldAuthorizationEvaluator authEvaluator,
    IActingUserAccessor actingUserAccessor)
    : IEntityRelationResolver
{
    public async Task ResolveRelationsAsync(Struct entityStruct, SchemaDescriptor schema, int depth, CancellationToken ct)
    {
        foreach (var relation in schema.Relations)
        {
            switch (relation.Kind)
            {
                case SchemaRelationKind.ManyToOne:
                case SchemaRelationKind.OneToOne:
                    await ResolveSingleRelationAsync(entityStruct, relation, depth, ct);
                    break;
                case SchemaRelationKind.ManyToMany:
                    await ResolveManyToManyAsync(entityStruct, relation, depth, ct);
                    break;
                case SchemaRelationKind.OneToMany:
                    await ResolveOneToManyAsync(entityStruct, schema, relation, depth, ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(relation.Kind), relation.Kind,
                        $"Unhandled {nameof(SchemaRelationKind)} value in relation resolution — add a case above.");
            }
        }
    }

    // ResolveSingleRelationAsync / ResolveManyToManyAsync / ResolveOneToManyAsync move here
    // verbatim from ObjectMappingGrpcService, each replacing its inline auth-check-and-mask
    // block with:
    private bool TryAuthorizeAndMask(Struct relatedStruct, AuthorizationDecision decision)
    {
        if (decision.Denied ||
            (decision.OwnershipRequired &&
             StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue))
            return false;
        AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);
        return true;
    }

    // FetchByKeyAsync-equivalent private helper moves here too — only this collaborator's
    // methods still call it after the move.
}
```

`ObjectMappingGrpcService.Get` changes its `if (request.Depth > 0)` branch to call `await relationResolver.ResolveRelationsAsync(entityStruct, schema, request.Depth, context.CancellationToken);`.

**Verified:** no caller outside `ObjectMappingGrpcService` references `ResolveRelationsAsync` or its siblings (`grep` across the whole repo, excluding the file itself, returned no matches) — safe to move wholesale. `StructFieldAccess` and `AuthorizationFieldMasking` are both `internal static class` in `Iverson.Api.Grpc` — accessible from the new sibling class in the same assembly/namespace.

**Tests:** the 3 existing tests in `ObjectMappingGrpcServiceTests.cs` (`Get_WithDepth1_ResolvesManyToOneRelation`, `Get_WithDepth0_DoesNotResolveRelations`, `Get_WithManyToManyRelation_IssuesSingleBatchQuery`) move to a new `EntityRelationResolverTests.cs`, now calling `ResolveRelationsAsync` directly. Verified these tests construct the service directly with NSubstitute mocks (not `WebApplicationFactory`), so porting them to construct the new collaborator directly is a mechanical change of what's mocked, not a rewrite. `ObjectMappingGrpcServiceTests` keeps those 3 as thin checks that `Get` calls the resolver when `Depth > 0` and skips it at `Depth == 0`.

## Section D: `SchemaRegistrationOrchestrator`

**Problem:** `RegisterSchema`'s body (identifier validation, owner-field/string-type/chunk-key cross-checks, `SchemaBuilder.BuildDescriptor`, the four-store apply sequence) is a separate responsibility axis from CRUD, with its own 5 dependencies (`IRecordStoreSchemaManager`, `IVectorSchemaManager`, `IEngagementStoreSchemaManager`, `IEmbeddingService`; the 5th, `IEventProducer`, is already absorbed by `OutboxPublisher` from Section B — `RegisterSchema` never used it directly).

**Verified:** `SchemaRequest` has `root_type` (`TypeDescriptor`) and `dependents` (`repeated TypeDescriptor`); `SchemaResponse` has `registered` (`repeated string`) — matches the sketch below exactly (`docs/../Iverson.Clients/Common/Proto/object_mapping.proto:95-106`).

```csharp
// Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs
public interface ISchemaRegistrationOrchestrator
{
    Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct);
}

public sealed class SchemaRegistrationOrchestrator(
    IRecordStoreSchemaManager schemaManager,
    IVectorSchemaManager vector,
    IEngagementStoreSchemaManager starRocks,
    IEmbeddingService embedding,
    SchemaRegistry registry,
    ILogger<SchemaRegistrationOrchestrator> logger)
    : ISchemaRegistrationOrchestrator
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    // Deliberately does NOT log "[RegisterSchema] root=... dependents=..." — that line stays in
    // ObjectMappingGrpcService.RegisterSchema (see the prose above this sketch), since it reads
    // directly off the raw request and needs nothing this orchestrator owns.
    public async Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct)
    {
        var registered = new List<string>();
        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            // ValidateIdentifier + owner-field/string-type/chunk-key checks + BuildDescriptor +
            // apply-to-4-stores sequence, moved verbatim from RegisterSchema's loop body.
        }
        return registered;
    }

    private static void ValidateIdentifier(string name, string context) { /* moved verbatim */ }
}
```

`ObjectMappingGrpcService.RegisterSchema` shrinks to: the existing pre-loop `_logger.LogInformation("[RegisterSchema] root={Type} dependents={Deps}", ...)` line (kept first, in its current position — it reads directly off the raw `request` and needs nothing from the orchestrator), the `request.RootType is null` guard, `await schemaRegistration.RegisterAsync(request, context.CancellationToken)`, and building the `SchemaResponse`. The `[Authorize(Policy = "SchemaAdmin")]` attribute stays on the gRPC method (ASP.NET Core's authorization pipeline, not the orchestrator's concern).

**Convention confirmed:** `RelationValidator` (an existing extracted collaborator) throws `RpcException` directly rather than a translated domain exception (confirmed via grep). `EntityRelationResolver` and `SchemaRegistrationOrchestrator` follow the same convention — no new exception-translation layer introduced.

**Tests:** all 13 existing `RegisterSchema_*` tests in `ObjectMappingGrpcServiceTests.cs` move to a new `SchemaRegistrationOrchestratorTests.cs`, now calling `RegisterAsync` directly. `ObjectMappingGrpcServiceTests` keeps 2 thin checks: the null-`RootType` guard (stays in the gRPC method) and one happy-path test confirming `RegisterSchema` returns the orchestrator's result correctly.

## Section F: DI Registration

All 3 new collaborators registered `AddSingleton`, matching the existing convention for gRPC-layer collaborators (confirmed: `IRelationValidator`, `IRowFieldAuthorizationEvaluator`, `IEntityKeyAccessor`, `IOutboxWriter` are all `AddSingleton` in `Program.cs:177-180`):

```csharp
builder.Services.AddSingleton<IOutboxPublisher, OutboxPublisher>();
builder.Services.AddSingleton<IEntityRelationResolver, EntityRelationResolver>();
builder.Services.AddSingleton<ISchemaRegistrationOrchestrator, SchemaRegistrationOrchestrator>();
```

## Net Effect on `ObjectMappingGrpcService`

Constructor drops from 14 raw dependencies to 9 raw + 3 collaborators (12 total). Removed entirely: `IRecordStoreSchemaManager`, `IVectorSchemaManager`, `IEngagementStoreSchemaManager`, `IEmbeddingService`, `IEventProducer` (absorbed into the new collaborators). Remain (still needed directly by CRUD): `IEntityRepository`, `IRecordStoreTransactionRunner`, `IRelationValidator`, `IEntityKeyAccessor`, `IOutboxWriter`, `SchemaRegistry`, `IActingUserAccessor`, `IRowFieldAuthorizationEvaluator`, `ILogger`. The count reduction is modest — the real gain is cohesion: every remaining raw dependency and every collaborator now serves CRUD orchestration directly, versus 14 undifferentiated dependencies all reachable from every method today.

`ObjectSearchGrpcService`'s constructor is unchanged — Finding #1 is a call-site edit, not a dependency change.

## Out of Scope

- Finding #5 (the `ProtoKindToSr`/`SrKindToProto` parallel-switch OCP cleanup in `ObjectSearchGrpcService`) — cosmetic, no defect history, excluded from this design at the user's direction.

## Verified Assumptions

| # | Assumption | Result |
|---|---|---|
| 1 | `Iverson.Vector.Tests` exists and can host `QdrantFilterBuilder` tests | Confirmed — `QdrantFilterBuilderTests.cs` already exists; new tests are additions to it |
| 2 | `ObjectPersistenceGrpcService`'s duplication matches `ObjectMappingGrpcService`'s shape | Confirmed, but found a 5th site (`.Update`, not just `.Post`) — design updated, re-approved by user |
| 3 | `StoreTargeting`/`EntityTopics` accessible from `Iverson.Api/Grpc` | Confirmed — `internal`/`public` in the same or a referenced assembly |
| 4 | No caller outside `ObjectMappingGrpcService` uses the private helpers being moved | Confirmed via repo-wide grep — no matches |
| 5 | `StructFieldAccess`/`AuthorizationFieldMasking` accessible from a new sibling class | Confirmed — both `internal static class` in the same assembly |
| 6 | `SchemaRequest`/`SchemaResponse` proto shape matches the sketch | Confirmed against `object_mapping.proto:95-106` |
| 7 | Existing tests construct the service directly (not via `WebApplicationFactory`) | Confirmed — NSubstitute mocks, direct construction |
| — | (mechanical correction) `DetermineTargetStores` returns `StoreTarget`, not a list | Confirmed via grep — `[Flags]` enum, singular value |
