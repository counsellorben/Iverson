# SOLID Remediation for ObjectSearchGrpcService & ObjectMappingGrpcService Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Source spec:** `docs/specs/2026-07-16-search-mapping-grpc-solid-remediation-design.md` (commit SHA: `69eb0980a4e19e13f2b527f6a30ff5c2d92aeae7`)

**Goal:** Close 4 SOLID/DRY findings in `ObjectSearchGrpcService` and `ObjectMappingGrpcService` via 4 additive, behavior-preserving extractions — no functional change to any RPC's external behavior.

**Architecture:** Four new collaborators, each closing one duplication/responsibility-concentration finding: `QdrantFilterBuilder.ApplyOwnership` (static method, closes Qdrant ownership-filter duplication), `OutboxPublisher` (closes the 5x-duplicated Kafka-publish block), `EntityRelationResolver` (extracts relation-graph resolution out of `ObjectMappingGrpcService`), `SchemaRegistrationOrchestrator` (extracts schema/DDL registration out of `ObjectMappingGrpcService`). All 3 non-static collaborators are registered `AddSingleton` and injected into the gRPC services they replace logic in.

**Tech stack:** C# / .NET 10 (net10.0), gRPC (`Grpc.Core`), xUnit + FluentAssertions + NSubstitute for tests, Qdrant.Client.Grpc for vector filtering.

---

## File Structure

- Create: `Iverson.Server/Iverson.Api/Grpc/OutboxPublisher.cs` — `IOutboxPublisher`/`OutboxPublisher`, dedupes the Kafka-publish-then-cleanup block
- Create: `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs` — `IEntityRelationResolver`/`EntityRelationResolver`, relation-graph resolution
- Create: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs` — `ISchemaRegistrationOrchestrator`/`SchemaRegistrationOrchestrator`, schema/DDL registration
- Modify: `Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs` — add `ApplyOwnership` static method
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs` — 2 call sites use `ApplyOwnership`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` — Post/Update/Delete use `IOutboxPublisher`; Get delegates to `IEntityRelationResolver`; `RegisterSchema` shrinks to delegate to `ISchemaRegistrationOrchestrator`; constructor drops 5 dependencies, gains 3 collaborators
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` — Post/Update use `IOutboxPublisher`; constructor drops `IEventProducer`, gains `IOutboxPublisher`
- Modify: `Iverson.Server/Iverson.Api/Program.cs` — 3 new `AddSingleton` DI registrations
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs` — add `ApplyOwnership` tests
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/OutboxPublisherTests.cs` — new
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/EntityRelationResolverTests.cs` — new (3 tests moved from `ObjectMappingGrpcServiceTests.cs`)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs` — new (14 tests moved from `ObjectMappingGrpcServiceTests.cs`)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` — shared `_sut` constructor updated; moved test bodies removed; thin delegation checks added
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` — shared `_sut` constructor updated

## Inherited from spec

The following were verified by `thorough-brainstorming` at spec-write time and are NOT re-verified here:

- `Iverson.Vector` does not reference `Iverson.Api` (`Iverson.Vector.csproj` only references `Iverson.Client.Contracts`) — `ApplyOwnership` must take primitives, not `AuthorizationDecision`/`SchemaDescriptor`
- `StoreTargeting.DetermineTargetStores` returns a single `StoreTarget` (a `[Flags]` enum), not a list
- `ObjectPersistenceGrpcService`'s duplication matches `ObjectMappingGrpcService`'s shape at 5 total call sites (`.Post`/`.Update` on `ObjectMappingGrpcService`'s sibling plus its own `.Post`/`.Update`, plus `ObjectMappingGrpcService.Delete`) — confirmed no `Delete` method exists on `ObjectPersistenceGrpcService`
- `StoreTargeting`/`EntityTopics` are accessible from `Iverson.Api/Grpc` (internal/public in the same or a referenced assembly)
- No caller outside `ObjectMappingGrpcService` uses `ResolveRelationsAsync` or its siblings (repo-wide grep, excluding the file itself)
- `StructFieldAccess`/`AuthorizationFieldMasking` are both `internal static class` in `Iverson.Api.Grpc` — accessible from new sibling classes in the same assembly/namespace
- `SchemaRequest`/`SchemaResponse` proto shape matches `object_mapping.proto:95-106` (`root_type`, `dependents`, `trace_id` / `success`, `trace_id`, `error`, `registered`)
- Existing gRPC service tests construct the service directly with NSubstitute mocks, not `WebApplicationFactory`
- `RelationValidator` (an existing extracted collaborator) throws `RpcException` directly rather than a translated domain exception — the new collaborators follow the same convention, no exception-translation layer introduced
- `FetchByKeyAsync` (the private helper) has exactly 4 callers pre-plan: `Get`/`Update`/`Delete` (staying) plus `ResolveSingleRelationAsync` (moving, inlined instead)

## Verified plan-level assumptions

| # | Category | Assumption | Evidence |
|---|---|---|---|
| 1 | File path | None of `OutboxPublisher.cs`, `EntityRelationResolver.cs`, `SchemaRegistrationOrchestrator.cs`, or their 3 test files exist yet | `find`/`ls` on `Iverson.Server/Iverson.Api/Grpc/` and `Iverson.Server/Iverson.Api.Tests/Grpc/` — none present |
| 2 | Function signature | `IEventProducer.ProduceAsync<T>(string topic, string key, T message) where T : class` generic overload exists | Read `Iverson.Events/IEventProducer.cs:5` |
| 3 | Function signature | `IOutboxWriter.DeleteOutboxRowIfPresentAsync(Guid outboxRowId)` | Read `Iverson.Sql/OutboxWriter.cs:6` |
| 4 | Function signature | `EntityEvent` positional record: `(EntityEventType, string TypeName, string Key, string PayloadJson, string TraceId, string SchemaVersion, DateTimeOffset OccurredAt, StoreTarget TargetStores = StoreTarget.All)` | Read `Iverson.Events/EntityEvent.cs:20-28` |
| 5 | Function signature | `IRowFieldAuthorizationEvaluator.Evaluate(SchemaDescriptor, ClaimsPrincipal?, AuthorizationAction)` returns `AuthorizationDecision(bool Denied, bool OwnershipRequired, string? OwnerFieldName, string? OwnerValue, IReadOnlySet<string>? AllowedFields)` | Read `Iverson.Api/Authorization/IRowFieldAuthorizationEvaluator.cs:8-27` |
| 6 | Function signature | `IEntityRepository.FetchByKeyAsync/FetchManyByKeysAsync/FetchByColumnAsync` signatures match usage in the code being moved | Read `Iverson.Sql/IRecordStoreRoles.cs:27-34` |
| 7 | Function signature | `StructFieldAccess.GetFieldString`/`GetFieldStringList` and `AuthorizationFieldMasking.MaskDisallowedFields(Struct, IReadOnlySet<string>?, string? exemptField = null)` signatures match usage in the moved relation code | Read `Iverson.Api/Grpc/StructFieldAccess.cs`, `AuthorizationFieldMasking.cs:73` |
| 8 | Test/build command | `dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj` then `dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~<Class>"` run from `Iverson.Server/`, per prior plans' working invocations against this exact project | `docs/plans/2026-07-14-row-field-authorization-evaluator-fixes-implementation-plan.md:57`; `Iverson.Api.Tests.csproj:30` references `Iverson.Api.csproj` |
| 9 | Test/build command | `dotnet build Iverson.Vector.Tests/Iverson.Vector.Tests.csproj` then `dotnet test ... --filter "FullyQualifiedName~QdrantFilterBuilderTests"` from `Iverson.Server/` | `Iverson.Vector.Tests.csproj:26` references `Iverson.Vector.csproj`; same convention as #8 |
| 10 | Ordering | Task 1 touches only `Iverson.Vector/QdrantFilterBuilder.cs` + `ObjectSearchGrpcService.cs` — no file overlap with Tasks 2-4 | File-structure diff above |
| 11 | Ordering | Tasks 2, 3, 4 each modify `ObjectMappingGrpcService.cs`'s constructor and `ObjectMappingGrpcServiceTests.cs`'s shared `_sut` constructor, in disjoint method regions (Post/Update/Delete vs. Get vs. RegisterSchema) — no task's code references a symbol a *later* task introduces | Read of `ObjectMappingGrpcService.cs` full file; each task's new dependency is consumed only within that task's own method region |
| 12 | Code validity | `file static class` (file-scoped types, used for the moved `StringExtensions.NullIfEmpty`) is supported — requires C# 11+ | `Iverson.Api.csproj:4` — `TargetFramework net10.0`; existing code already uses `file static class` twice (`ObjectMappingGrpcService.cs:550`, `ObjectPersistenceGrpcService.cs:179`) |
| 13 | Consumer impact | `IEventProducer` has no usage in `ObjectMappingGrpcService.cs`/`ObjectPersistenceGrpcService.cs` outside the 5 moving blocks — safe to remove from both constructors | `grep -n "_events\.\|events\."` in both files: all hits are inside the moving `try`/`catch` blocks |
| 14 | Consumer impact | `IRecordStoreSchemaManager`, `IVectorSchemaManager`, `IEngagementStoreSchemaManager`, `IEmbeddingService` have no usage in `ObjectMappingGrpcService.cs` outside `RegisterSchema` — safe to remove from the constructor | Read of full file: `_schemaManager` only at line 108, `_vector` only at 121/124, `_starRocks` only at 112, `_embedding` only at 66 — all inside `RegisterSchema` |
| 15 | Consumer impact | `StringExtensions.NullIfEmpty` (file-scoped, duplicated in both service files) has no usage outside the moving blocks in either file | `grep -n "NullIfEmpty"` repo-wide: every call site is inside a moving block; each file's definition is otherwise unreferenced |
| 16 | Consumer impact | `RequireSchema` and `IdentifierPattern`/`ValidateIdentifier` are not used by the relation-resolution code being moved (Task 3) or outside `RegisterSchema` (Task 4) respectively | `grep -n "RequireSchema("` — only Get/Post/Update/Delete (147/187/253/317) plus its own definition; `grep -n "IdentifierPattern\|ValidateIdentifier("` — only within `RegisterSchema` (62/64) plus definitions |
| 17 | Consumer impact | `_events` mock usage in both test files (setup + `.Received()`/`.DidNotReceive()` assertions) references the same mock object regardless of whether it's passed directly to the SUT or wrapped in a real `OutboxPublisher` — existing assertions keep working unchanged once the shared `_sut` is wired with a real `OutboxPublisher(_events, ...)` | `grep -n "_events\."` in both test files: 11 + 7 hits, all against the `_events` field itself, none inspecting how it's wired into the SUT |
| 18 | Consumer impact (test) | Beyond the 3 tests the spec names, 2 more existing tests (`Get_WithRelatedEntities_OmitsDeniedRelatedEntity_KeepsAllowedOne`, `Get_WithFieldPermissionExcludingFkColumn_OmitsRelatedEntity`) call `_sut.Get(..., Depth = 1)` and assert on real recursive relation-resolution/masking output — the shared `_sut` must keep a **real** `EntityRelationResolver`, not a mock | `grep -n "Depth = 1"` in `ObjectMappingGrpcServiceTests.cs` — 5 hits total (744/757, 766/775, 786/806 already known; 969/989 and 999/1027 are the 2 additional ones), read of both additional tests confirms real-data assertions |
| 19 | Mechanical correction | Spec cites `ObjectPersistenceGrpcService.Post`'s block as lines 63-90; the actual block (from `var published = false;` through the second `catch`'s closing brace) is lines 63-92 | Read of `ObjectPersistenceGrpcService.cs:63-92` |
| 20 | Mechanical correction | Spec's testing note undercounts by one: 15 `RegisterSchema_*` tests exist in `ObjectMappingGrpcServiceTests.cs`, not 13+2=15 as implied — 14 non-null-guard tests exist, not 13 | `grep -n "public.*RegisterSchema_"` — 15 matches enumerated and read in full (lines 156-379) |
| 21 | Consumer impact / DI lifetime | `EntityRelationResolver`'s 3 constructor dependencies (`SchemaRegistry`, `IEntityRepository`, `IRowFieldAuthorizationEvaluator`) are all `AddSingleton` — safe for its own `AddSingleton` registration. `IActingUserAccessor` is `AddScoped` (`Program.cs:149`, holds mutable per-request identity) and is deliberately excluded from the constructor for this reason — passed as a call-time `ClaimsPrincipal?` parameter instead (Task 3 Step 1) | `critical-implementation-review` round 1, finding 2.1 + §3.1 resolution (option b); `Program.cs:149` (`AddScoped<IActingUserAccessor, ActingUserAccessor>`) |

## Tasks

### Task 1: `QdrantFilterBuilder.ApplyOwnership`

**Files:**
- Modify: `Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs:154-158` (`SearchSimilar`), `:225-229` (`SearchChunks`)
- Test: `Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs`

- [ ] **Step 1: Add `ApplyOwnership` tests to `QdrantFilterBuilderTests.cs`**

Append to the existing test class, matching its established `FluentAssertions` + `[Fact]` style:

```csharp
[Fact]
public void ApplyOwnership_NotRequired_NullFilter_ReturnsNull()
{
    var result = QdrantFilterBuilder.ApplyOwnership(null, ownershipRequired: false, "ownerId", "owner-1");

    result.Should().BeNull();
}

[Fact]
public void ApplyOwnership_NotRequired_ExistingFilter_ReturnsSameFilterUnchanged()
{
    var original = new Filter();
    original.Must.Add(Conditions.MatchKeyword("category", "Tech"));

    var result = QdrantFilterBuilder.ApplyOwnership(original, ownershipRequired: false, "ownerId", "owner-1");

    result.Should().BeSameAs(original);
    result!.Must.Should().ContainSingle();
}

[Fact]
public void ApplyOwnership_Required_NullFilter_CreatesFilterWithMatchKeywordCondition()
{
    var result = QdrantFilterBuilder.ApplyOwnership(null, ownershipRequired: true, "ownerId", "owner-1");

    result.Should().NotBeNull();
    result!.Must.Should().ContainSingle();
    result.Must[0].Field.Key.Should().Be("ownerId");
    result.Must[0].Field.Match.Keyword.Should().Be("owner-1");
}

[Fact]
public void ApplyOwnership_Required_ExistingFilter_AppendsConditionPreservingExisting()
{
    var original = new Filter();
    original.Must.Add(Conditions.MatchKeyword("category", "Tech"));

    var result = QdrantFilterBuilder.ApplyOwnership(original, ownershipRequired: true, "ownerId", "owner-1");

    result.Should().BeSameAs(original);
    result!.Must.Should().HaveCount(2);
}
```

- [ ] **Step 2: Implement `ApplyOwnership` in `QdrantFilterBuilder.cs`**

Add alongside `Build`/`MatchParentId`:

```csharp
public static Filter? ApplyOwnership(Filter? filter, bool ownershipRequired, string? ownerFieldCamelCase, string? ownerValue)
{
    if (!ownershipRequired) return filter;
    filter ??= new Filter();
    filter.Must.Add(Conditions.MatchKeyword(ownerFieldCamelCase!, ownerValue!));
    return filter;
}
```

- [ ] **Step 3: Update `ObjectSearchGrpcService.cs` call sites**

Replace `SearchSimilar`'s block at lines 154-158:
```csharp
        if (decision.OwnershipRequired)
        {
            filter ??= new Filter();
            filter.Must.Add(Conditions.MatchKeyword(schema.Authorization!.OwnerField!.ToCamelCase(), decision.OwnerValue!));
        }
```
with:
```csharp
        filter = QdrantFilterBuilder.ApplyOwnership(filter, decision.OwnershipRequired, schema.Authorization?.OwnerField?.ToCamelCase(), decision.OwnerValue);
```

Apply the identical replacement to `SearchChunks`'s block at lines 225-229. Use `?.`, not `!.`, on `schema.Authorization`/`OwnerField` in both call sites — arguments are evaluated eagerly, and a bypass-role caller on a schema with no `OwnerField` would otherwise hit a `NullReferenceException` before `ApplyOwnership`'s own `if (!ownershipRequired)` guard runs.

- [ ] **Step 4: Build, test, commit**

```bash
cd Iverson.Server
dotnet build Iverson.Vector.Tests/Iverson.Vector.Tests.csproj
dotnet test Iverson.Vector.Tests/Iverson.Vector.Tests.csproj --filter "FullyQualifiedName~QdrantFilterBuilderTests"
dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj
dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~ObjectSearchGrpcServiceTests"
git add Iverson.Server/Iverson.Vector/QdrantFilterBuilder.cs Iverson.Server/Iverson.Vector.Tests/QdrantFilterBuilderTests.cs Iverson.Server/Iverson.Api/Grpc/ObjectSearchGrpcService.cs
git commit -m "refactor(vector): extract QdrantFilterBuilder.ApplyOwnership, use it in SearchSimilar/SearchChunks"
```

---

### Task 2: `OutboxPublisher`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/OutboxPublisher.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (Post block `213-242`, Update block `278-307`, Delete block `359-388`, constructor, `SchemaVersion` const at line 43, `StringExtensions` at line 550-554)
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs` (Post block `63-92`, Update block `135-164`, constructor, `SchemaVersion` const at line 30, `StringExtensions` at line 179-183)
- Modify: `Iverson.Server/Iverson.Api/Program.cs` (after line 183)
- Test: `Iverson.Server/Iverson.Api.Tests/Grpc/OutboxPublisherTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs` (constructor only)
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs` (constructor only)

- [ ] **Step 1: Write `OutboxPublisherTests.cs`**

```csharp
using System.Diagnostics;
using FluentAssertions;
using Iverson.Api.Grpc;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class OutboxPublisherTests
{
    private readonly IEventProducer _events = Substitute.For<IEventProducer>();
    private readonly IOutboxWriter _outboxWriter = Substitute.For<IOutboxWriter>();
    private readonly ILogger<OutboxPublisher> _logger = Substitute.For<ILogger<OutboxPublisher>>();
    private readonly OutboxPublisher _sut;

    public OutboxPublisherTests()
    {
        _sut = new OutboxPublisher(_events, _outboxWriter, _logger);
    }

    private void AssertWarningLogged(string expectedSubstring) =>
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains(expectedSubstring)),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Fact]
    public async Task PublishAsync_Success_DeletesOutboxRowAndLogsNothing()
    {
        var outboxRowId = Guid.NewGuid();

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", "trace-1",
            StoreTarget.All, outboxRowId, "Mapping.Post");

        await _outboxWriter.Received(1).DeleteOutboxRowIfPresentAsync(outboxRowId);
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_ProduceThrows_LogsOpportunisticFailureWarning_DoesNotDeleteOutboxRow()
    {
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>())
            .Returns<Task>(_ => throw new InvalidOperationException("kafka down"));

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", "trace-1",
            StoreTarget.All, Guid.NewGuid(), "Mapping.Post");

        await _outboxWriter.DidNotReceive().DeleteOutboxRowIfPresentAsync(Arg.Any<Guid>());
        AssertWarningLogged("Opportunistic publish failed");
    }

    [Fact]
    public async Task PublishAsync_DeleteThrows_LogsCleanupFailureWarning()
    {
        var outboxRowId = Guid.NewGuid();
        _outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId)
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", "trace-1",
            StoreTarget.All, outboxRowId, "Mapping.Post");

        AssertWarningLogged("Publish succeeded but outbox cleanup failed");
    }

    [Theory]
    [InlineData("caller-trace", "caller-trace")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public async Task PublishAsync_RequestTraceIdPresentOrAbsentNoActivity_ResolvesExpectedTraceId(
        string? requestTraceId, string expected)
    {
        EntityEvent? captured = null;
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
            .Returns(Task.CompletedTask);

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", requestTraceId,
            StoreTarget.All, Guid.NewGuid(), "Mapping.Post");

        captured!.TraceId.Should().Be(expected);
    }

    [Fact]
    public async Task PublishAsync_NoRequestTraceId_ActivityCurrentPresent_UsesActivityTraceId()
    {
        using var activitySource = new ActivitySource(nameof(OutboxPublisherTests));
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = activitySource.StartActivity("test-activity");

        EntityEvent? captured = null;
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
            .Returns(Task.CompletedTask);

        await _sut.PublishAsync(EntityEventType.Created, "Widget", "key-1", "{}", requestTraceId: null,
            StoreTarget.All, Guid.NewGuid(), "Mapping.Post");

        captured!.TraceId.Should().Be(Activity.Current!.TraceId.ToString());
    }
}
```

- [ ] **Step 2: Implement `OutboxPublisher.cs`**

```csharp
using System.Diagnostics;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Grpc;

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
        string opLabel,
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

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
```

- [ ] **Step 3: Update `ObjectMappingGrpcService.cs`**

Constructor: replace `IEventProducer _events` (5th parameter) in place with `IOutboxPublisher _outboxPublisher` — same position, so every other parameter's position is unaffected. Remove the `private const string SchemaVersion = "1";` (line 43) and the trailing `file static class StringExtensions { NullIfEmpty }` block (lines 550-554) — both now dead.

In `Post`, replace lines 213-242 (from `var published = false;` through the closing brace of the second `catch`) with:
```csharp
        await _outboxPublisher.PublishAsync(EntityEventType.Created, request.TypeName, key, payloadJson,
            request.TraceId, targetStores, outboxRowId, "Mapping.Post");
```
Apply the same replacement shape to `Update` (lines 278-307, `EntityEventType.Updated`, `opLabel: "Mapping.Update"`) and `Delete` (lines 359-388, `EntityEventType.Deleted`, key/payload args `request.Key`/`rowJson`, `opLabel: "Mapping.Delete"`).

- [ ] **Step 4: Update `ObjectPersistenceGrpcService.cs`**

Same shape: constructor replaces `IEventProducer events` (1st parameter) in place with `IOutboxPublisher outboxPublisher` — same position. Remove `SchemaVersion` const (line 30) and the trailing `StringExtensions` block (lines 179-183). Replace `Post`'s block (lines 63-92) and `Update`'s block (lines 135-164) with `await outboxPublisher.PublishAsync(...)` calls using `opLabel: "Persistence.Post"` / `"Persistence.Update"` respectively.

- [ ] **Step 5: Register in `Program.cs`**

After the `IOutboxWriter` registration (line 183), before `IReconciliationQueueRepository` (line 184):
```csharp
builder.Services.AddSingleton<IOutboxPublisher, OutboxPublisher>();
```

- [ ] **Step 6: Update shared test constructors**

In `ObjectMappingGrpcServiceTests.cs`, add a new field alongside the other private readonly fields (needed as a field, not a constructor-local, because Task 3's and Task 4's thin delegation-check tests also reference it when building their own locally-scoped `sut`):
```csharp
private readonly IOutboxPublisher _outboxPublisher;
```
In the test class's constructor, before the `_sut = new ObjectMappingGrpcService(...)` line:
```csharp
_outboxPublisher = new OutboxPublisher(_events, new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner), NullLogger<OutboxPublisher>.Instance);
```
and replace the `_events` positional argument (5th) in the `_sut` constructor call with `_outboxPublisher`. This keeps every existing `_events.When(...)`/`.Received()`/`.DidNotReceive()` assertion working unchanged, since `_events` is still the mock underneath.

Apply the identical field-based change to `ObjectPersistenceGrpcServiceTests.cs`'s `_sut` construction (replacing its 1st positional argument, `events`).

- [ ] **Step 7: Build, test, commit**

```bash
cd Iverson.Server
dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj
dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~OutboxPublisherTests|FullyQualifiedName~ObjectMappingGrpcServiceTests|FullyQualifiedName~ObjectPersistenceGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/OutboxPublisher.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Grpc/ObjectPersistenceGrpcService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Grpc/OutboxPublisherTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectPersistenceGrpcServiceTests.cs
git commit -m "refactor(api): extract OutboxPublisher, dedupe publish-block across 5 call sites"
```

---

### Task 3: `EntityRelationResolver`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (`Get` method line 175-176; remove `ResolveRelationsAsync`+siblings, lines 423-547)
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Create: `Iverson.Server/Iverson.Api.Tests/Grpc/EntityRelationResolverTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `SchemaRegistry`, `IEntityRepository`, `IRowFieldAuthorizationEvaluator` as constructor dependencies — all already present in `ObjectMappingGrpcServiceTests`'s shared test fields. `IActingUserAccessor` is deliberately **not** a constructor dependency (see the DI-lifetime note in Step 1) — the caller passes `ClaimsPrincipal? actingUser` explicitly into `ResolveRelationsAsync` instead.
- Produces: `IEntityRelationResolver` — used only within this task; no later task depends on it

- [ ] **Step 1: Implement `EntityRelationResolver.cs`**

Move `ResolveRelationsAsync`, `ResolveSingleRelationAsync`, `ResolveManyToManyAsync`, `ResolveOneToManyAsync` (current lines 423-547) verbatim into the new class, with three changes: `ResolveSingleRelationAsync`'s inline auth-check-and-mask block collapses to a `TryAuthorizeAndMask` helper; its `FetchByKeyAsync(relatedSchema, fkValue)` call (currently the private helper) becomes `entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(relatedSchema), fkValue)` directly, since the private helper stays on `ObjectMappingGrpcService` and isn't visible here; and `IActingUserAccessor` is **not** taken as a constructor dependency — `ResolveRelationsAsync` and all 3 siblings instead take `ClaimsPrincipal? actingUser` as a method parameter, threaded through every recursive call. This is a deliberate deviation from `IActingUserAccessor`-as-constructor-dependency: that service is registered `AddScoped` (`Program.cs:149`, holds genuinely mutable per-request identity state set by `ActingUserInterceptor`), and `EntityRelationResolver` is registered `AddSingleton` (Step 3) — capturing a Scoped dependency in a Singleton's constructor is a captive-dependency bug (crashes under DI scope validation, or silently freezes the first caller's identity into every later call otherwise). Taking `actingUser` as a call-time parameter instead — the same pattern `IRowFieldAuthorizationEvaluator.Evaluate` itself already uses — keeps this collaborator genuinely stateless and safe to register `AddSingleton`, consistent with the other 3 cited "existing convention" singletons.

```csharp
using System.Security.Claims;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;
using Iverson.Sql;
using SchemaRelationKind       = Iverson.Api.Schema.RelationKind;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;

namespace Iverson.Api.Grpc;

public interface IEntityRelationResolver
{
    Task ResolveRelationsAsync(Struct entityStruct, SchemaDescriptor schema, int depth, ClaimsPrincipal? actingUser, CancellationToken ct);
}

public sealed class EntityRelationResolver(
    SchemaRegistry registry,
    IEntityRepository entities,
    IRowFieldAuthorizationEvaluator authEvaluator)
    : IEntityRelationResolver
{
    public async Task ResolveRelationsAsync(Struct entityStruct, SchemaDescriptor schema, int depth, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        foreach (var relation in schema.Relations)
        {
            switch (relation.Kind)
            {
                case SchemaRelationKind.ManyToOne:
                case SchemaRelationKind.OneToOne:
                    await ResolveSingleRelationAsync(entityStruct, relation, depth, actingUser, ct);
                    break;
                case SchemaRelationKind.ManyToMany:
                    await ResolveManyToManyAsync(entityStruct, relation, depth, actingUser, ct);
                    break;
                case SchemaRelationKind.OneToMany:
                    await ResolveOneToManyAsync(entityStruct, schema, relation, depth, actingUser, ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(relation.Kind), relation.Kind,
                        $"Unhandled {nameof(SchemaRelationKind)} value in relation resolution — add a case above.");
            }
        }
    }

    private bool TryAuthorizeAndMask(Struct relatedStruct, AuthorizationDecision decision)
    {
        if (decision.Denied ||
            (decision.OwnershipRequired &&
             StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue))
            return false;
        AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);
        return true;
    }

    private async Task ResolveSingleRelationAsync(
        Struct entityStruct, SchemaRelationDescriptor relation, int depth, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        var fkValue = StructFieldAccess.GetFieldString(entityStruct, relation.ForeignKey);
        if (string.IsNullOrWhiteSpace(fkValue)) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rowJson = await entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(relatedSchema), fkValue);
        if (rowJson is null) return;

        var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);
        if (!TryAuthorizeAndMask(relatedStruct, decision)) return;

        if (depth > 1)
            await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct);

        entityStruct.Fields[relation.PropertyName] = Value.ForStruct(relatedStruct);
    }

    private async Task ResolveManyToManyAsync(
        Struct entityStruct, SchemaRelationDescriptor relation, int depth, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        var ids = StructFieldAccess.GetFieldStringList(entityStruct, relation.ForeignKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await entities.FetchManyByKeysAsync(SchemaBuilder.ToTableSchema(relatedSchema), ids);
        var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);

        var items = new List<Value>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            if (!rowsByKey.TryGetValue(id, out var row)) continue;
            var relatedStruct = JsonParser.Default.Parse<Struct>(row.Data);

            if (!TryAuthorizeAndMask(relatedStruct, decision)) continue;

            if (depth > 1)
                await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }

    private async Task ResolveOneToManyAsync(
        Struct entityStruct, SchemaDescriptor schema, SchemaRelationDescriptor relation, int depth, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        var keyValue = StructFieldAccess.GetFieldString(entityStruct, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(keyValue)) return;

        var relatedSchema = registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await entities.FetchByColumnAsync(
            SchemaBuilder.ToTableSchema(relatedSchema), relation.ForeignKey, keyValue);

        var decision = authEvaluator.Evaluate(relatedSchema, actingUser, AuthorizationAction.Read);

        var items = new List<Value>();
        foreach (var rowJson in rows)
        {
            if (ct.IsCancellationRequested) break;
            var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);

            if (!TryAuthorizeAndMask(relatedStruct, decision)) continue;

            if (depth > 1)
                await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, actingUser, ct);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }
}
```

Note: `TryAuthorizeAndMask` is a genuine behavior-preserving simplification of the 3 identical inline blocks the spec's Section C calls out (Finding #4) — each of the 3 methods above previously repeated the same 4-line `if (decision.Denied || ...) return/continue; MaskDisallowedFields(...);` shape.

- [ ] **Step 2: Update `ObjectMappingGrpcService.cs`**

Remove `ResolveRelationsAsync`/`ResolveSingleRelationAsync`/`ResolveManyToManyAsync`/`ResolveOneToManyAsync` (lines 423-547) and the now-unused `SchemaRelationKind`/`SchemaRelationDescriptor` using-aliases (lines 16-17) if nothing else in the file uses them (verify via `grep -n "SchemaRelationKind\|SchemaRelationDescriptor"` after removal — expect zero remaining hits). Add `IEntityRelationResolver _relationResolver` as a new final parameter, appended after the existing last parameter (`_authEvaluator`) — `ObjectMappingGrpcService` itself keeps `IActingUserAccessor _actingUserAccessor` unchanged (already a constructor dependency; unaffected by Step 1's change) and now passes `_actingUserAccessor.ActingUser` explicitly at the call site. Replace `Get`'s line 175-176:
```csharp
        if (request.Depth > 0)
            await ResolveRelationsAsync(entityStruct, schema, request.Depth, context.CancellationToken);
```
with:
```csharp
        if (request.Depth > 0)
            await _relationResolver.ResolveRelationsAsync(entityStruct, schema, request.Depth, _actingUserAccessor.ActingUser, context.CancellationToken);
```

- [ ] **Step 3: Register in `Program.cs`**

Directly after Task 2's `IOutboxPublisher` line:
```csharp
builder.Services.AddSingleton<IEntityRelationResolver, EntityRelationResolver>();
```

- [ ] **Step 4: Create `EntityRelationResolverTests.cs`**

Move the 3 named tests (`Get_WithDepth1_ResolvesManyToOneRelation`, `Get_WithDepth0_DoesNotResolveRelations`, `Get_WithManyToManyRelation_IssuesSingleBatchQuery`) from `ObjectMappingGrpcServiceTests.cs`, retargeted to construct `EntityRelationResolver` directly and call `ResolveRelationsAsync` on a `Struct` built from the parsed JSON, instead of going through `_sut.Get(...)`. Worked example for the first:

```csharp
using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class EntityRelationResolverTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly SchemaRegistry _registry;
    private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();
    private readonly EntityRelationResolver _sut;

    private static readonly ClaimsPrincipal ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass");
    private static readonly string AuthorId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string ArticleId = "22222222-0000-0000-0000-000000000002";
    private static readonly string AuthorJson  = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer"}""";
    private static readonly string ArticleJson = $$"""{"Id":"{{ArticleId}}","Title":"Hello","Body":"World","AuthorId":"{{AuthorId}}"}""";

    public EntityRelationResolverTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new EntityRelationResolver(_registry, _entities, _authEvaluator);
    }

    [Fact]
    public async Task ResolveRelationsAsync_WithDepth1_ResolvesManyToOneRelation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _entities.FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<string>())
            .Returns(AuthorJson);

        var entityStruct = JsonParser.Default.Parse<Struct>(ArticleJson);
        var schema = _registry.Get("Article")!;

        await _sut.ResolveRelationsAsync(entityStruct, schema, depth: 1, ActingUser, CancellationToken.None);

        entityStruct.Fields.Should().ContainKey("Author");
        entityStruct.Fields["Author"].StructValue.Fields["Name"].StringValue.Should().Be("Alice");
    }

    // ResolveRelationsAsync_WithDepth0_DoesNotResolveRelations and
    // ResolveRelationsAsync_WithManyToManyRelation_IssuesSingleBatchQuery follow the same
    // pattern: parse the root JSON into a Struct, call _sut.ResolveRelationsAsync directly,
    // assert on the mutated Struct's Fields — same assertions as the original tests
    // (Get_WithDepth0_DoesNotResolveRelations's `await _entities.Received(1).FetchByKeyAsync(...)`
    // and Get_WithManyToManyRelation_IssuesSingleBatchQuery's `await _entities.Received(1).FetchManyByKeysAsync(...)`
    // carry over unchanged since both assert against the same _entities mock).
}
```

- [ ] **Step 5: Update `ObjectMappingGrpcServiceTests.cs`**

Remove the 3 moved test bodies. Add a new field (same reasoning as `_outboxPublisher` in Task 2 — Task 4's thin check also needs to reference it):
```csharp
private readonly IEntityRelationResolver _relationResolver;
```
In the constructor, before `_sut = new ObjectMappingGrpcService(...)`:
```csharp
_relationResolver = new EntityRelationResolver(_registry, _entities, _authEvaluator);
```
and pass `_relationResolver` as the new final argument to `_sut`'s constructor — a **real** instance, not a mock, since `Get_WithRelatedEntities_OmitsDeniedRelatedEntity_KeepsAllowedOne` and `Get_WithFieldPermissionExcludingFkColumn_OmitsRelatedEntity` (assumption #18) assert on real recursive resolution output through the shared `_sut`. At this point in the task sequence (after Task 2, before Task 4), `_sut`'s full constructor call is:
```csharp
_sut = new ObjectMappingGrpcService(
    _entities, _txRunner, _schemaManager, _vector, _outboxPublisher, _registry, _embedding, _starRocks,
    new RelationValidator(_registry), new EntityKeyAccessor(),
    new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
    NullLogger<ObjectMappingGrpcService>.Instance,
    _actingUserAccessor, _authEvaluator, _relationResolver);
```

Replace the 3 removed tests' role with 2 thin delegation checks using a **separate, locally-constructed** `sut` with a mocked `IEntityRelationResolver` (NSubstitute call-verification needs a mock reference, which the shared `_sut`'s real resolver can't provide — every other argument reuses the class's real fields, matching the block above). Add `using System.Security.Claims;` to this file's using list — needed for `Arg.Any<ClaimsPrincipal?>()` below, matching `IEntityRelationResolver.ResolveRelationsAsync`'s updated signature from Step 1:

```csharp
[Fact]
public async Task Get_WithDepthGreaterThanZero_CallsRelationResolver()
{
    await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
    _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>()).Returns(ArticleJson);

    var mockResolver = Substitute.For<IEntityRelationResolver>();
    var sut = new ObjectMappingGrpcService(
        _entities, _txRunner, _schemaManager, _vector, _outboxPublisher, _registry, _embedding, _starRocks,
        new RelationValidator(_registry), new EntityKeyAccessor(),
        new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
        NullLogger<ObjectMappingGrpcService>.Instance,
        _actingUserAccessor, _authEvaluator, mockResolver);

    await sut.Get(new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 1 }, TestServerCallContext.Create());

    await mockResolver.Received(1).ResolveRelationsAsync(
        Arg.Any<Struct>(), Arg.Any<SchemaDescriptor>(), 1, Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task Get_WithDepthZero_DoesNotCallRelationResolver()
{
    await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
    _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>()).Returns(ArticleJson);

    var mockResolver = Substitute.For<IEntityRelationResolver>();
    var sut = new ObjectMappingGrpcService(
        _entities, _txRunner, _schemaManager, _vector, _outboxPublisher, _registry, _embedding, _starRocks,
        new RelationValidator(_registry), new EntityKeyAccessor(),
        new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
        NullLogger<ObjectMappingGrpcService>.Instance,
        _actingUserAccessor, _authEvaluator, mockResolver);

    await sut.Get(new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 0 }, TestServerCallContext.Create());

    await mockResolver.DidNotReceiveWithAnyArgs().ResolveRelationsAsync(
        default!, default!, default, default, default);
}
```

Only 2 thin checks are needed here, not 3 — `Get_WithManyToManyRelation_IssuesSingleBatchQuery` was testing `EntityRelationResolver`'s internal batching behavior, not `Get`'s delegation, so it has no thin-check equivalent; it fully moved to Step 4 and isn't replaced in this file.

- [ ] **Step 6: Build, test, commit**

```bash
cd Iverson.Server
dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj
dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~EntityRelationResolverTests|FullyQualifiedName~ObjectMappingGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/EntityRelationResolver.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Grpc/EntityRelationResolverTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "refactor(api): extract EntityRelationResolver from ObjectMappingGrpcService"
```

---

### Task 4: `SchemaRegistrationOrchestrator`

**Files:**
- Create: `Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs`
- Modify: `Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs` (`RegisterSchema` lines 47-136; `ValidateIdentifier`/`IdentifierPattern` lines 401-410)
- Modify: `Iverson.Server/Iverson.Api/Program.cs`
- Create: `Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs`
- Modify: `Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs`

**Interfaces:**
- Consumes: `IRecordStoreSchemaManager`, `IVectorSchemaManager`, `IEngagementStoreSchemaManager`, `IEmbeddingService`, `SchemaRegistry` — all already present in `ObjectMappingGrpcServiceTests`'s shared test fields
- Produces: `ISchemaRegistrationOrchestrator` — used only within this task

- [ ] **Step 1: Implement `SchemaRegistrationOrchestrator.cs`**

Move the per-type validation/build/apply loop body (current `RegisterSchema` lines 60-128) and `ValidateIdentifier`/`IdentifierPattern` (lines 401-410) verbatim:

```csharp
using System.Text.RegularExpressions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.StarRocks;
using Iverson.Vector;
using Grpc.Core;

namespace Iverson.Api.Grpc;

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

    public async Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct)
    {
        var registered = new List<string>();

        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            ValidateIdentifier(typeDesc.TypeName, "type_name");
            foreach (var property in typeDesc.Properties)
                ValidateIdentifier(property.Name, $"property name on type '{typeDesc.TypeName}'");

            var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

            var ownerField = descriptor.Authorization?.OwnerField;
            if (!string.IsNullOrEmpty(ownerField) &&
                !descriptor.ScalarColumns.Any(c => string.Equals(c.Name, ownerField, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"owner_field '{ownerField}' on '{descriptor.TypeName}' does not match any declared scalar property."));
            }

            if (!string.IsNullOrEmpty(ownerField))
            {
                var ownerColumn = descriptor.ScalarColumns.First(c =>
                    string.Equals(c.Name, ownerField, StringComparison.OrdinalIgnoreCase));

                var stringValuedSqlTypes = new[] { "TEXT", "UUID", "BYTEA", "TIMESTAMPTZ" };
                if (!stringValuedSqlTypes.Contains(ownerColumn.SqlType.ToUpperInvariant()))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"owner_field '{ownerField}' on '{descriptor.TypeName}' has SqlType '{ownerColumn.SqlType}', " +
                        "which is not string-valued; Qdrant ownership filtering requires a string-valued owner field."));
                }

                if (descriptor.ChunkFields.Count > 0)
                {
                    var reservedChunkKeys = new[] { "text", "parent_id", "field", "chunk_index" };
                    var camelOwnerField = ownerField.ToCamelCase();
                    if (reservedChunkKeys.Contains(camelOwnerField))
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                            $"owner_field '{ownerField}' on '{descriptor.TypeName}' camelCases to '{camelOwnerField}', " +
                            $"which collides with a reserved chunk-payload key ({string.Join(", ", reservedChunkKeys)})."));
                    }
                }
            }

            await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));

            try
            {
                await starRocks.ApplyTableAsync(SchemaBuilder.ToStarRocksTableSchema(descriptor));
            }
            catch (StarRocksNotReadyException ex)
            {
                throw new RpcException(new Status(StatusCode.Unavailable,
                    $"StarRocks is not ready: {ex.Message}"));
            }

            if (descriptor.VectorFields.Count > 0)
                await vector.ApplyCollectionAsync(SchemaBuilder.ToCollectionSchema(descriptor));

            if (descriptor.ChunkFields.Count > 0)
                await vector.ApplyCollectionAsync(SchemaBuilder.ToChunkCollectionSchema(descriptor));

            await registry.RegisterAsync(descriptor);
            registered.Add(descriptor.TypeName);
        }

        return registered;
    }

    private static void ValidateIdentifier(string name, string context)
    {
        if (!IdentifierPattern.IsMatch(name))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{context} '{name}' is not a valid identifier — it must start with a letter and contain only letters and digits."));
        }
    }
}
```

- [ ] **Step 2: Shrink `ObjectMappingGrpcService.RegisterSchema`**

Replace the method body (lines 48-136) with:
```csharp
    [Authorize(Policy = "SchemaAdmin")]
    public override async Task<SchemaResponse> RegisterSchema(
        SchemaRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[RegisterSchema] root={Type} dependents={Deps}",
            request.RootType?.TypeName?.SanitizeForLog(), request.Dependents.Count);

        if (request.RootType is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "root_type is required."));

        var registered = await _schemaRegistration.RegisterAsync(request, context.CancellationToken);

        return new SchemaResponse
        {
            Success    = true,
            TraceId    = request.TraceId,
            Registered = { registered }
        };
    }
```
Remove `ValidateIdentifier`/`IdentifierPattern` (lines 401-410) and the `IRecordStoreSchemaManager`/`IVectorSchemaManager`/`IEngagementStoreSchemaManager`/`IEmbeddingService` constructor parameters; add `ISchemaRegistrationOrchestrator _schemaRegistration` as a new final parameter, appended after `_relationResolver` (the parameter Task 3 added). The constructor's final shape (9 raw dependencies + 3 collaborators, matching the spec's "Net Effect" count) is: `_entities, _txRunner, _outboxPublisher, _registry, _relationValidator, _keyAccessor, _outboxWriter, _logger, _actingUserAccessor, _authEvaluator, _relationResolver, _schemaRegistration`.

- [ ] **Step 3: Register in `Program.cs`**

Directly after Task 3's `IEntityRelationResolver` line:
```csharp
builder.Services.AddSingleton<ISchemaRegistrationOrchestrator, SchemaRegistrationOrchestrator>();
```

- [ ] **Step 4: Create `SchemaRegistrationOrchestratorTests.cs`**

Move all 14 non-null-guard `RegisterSchema_*` tests from `ObjectMappingGrpcServiceTests.cs` (every test at lines 165-379 except `RegisterSchema_WithNullRootType_ThrowsInvalidArgument`), retargeted to construct `SchemaRegistrationOrchestrator` directly and call `RegisterAsync(request, CancellationToken.None)` instead of `_sut.RegisterSchema(request, context)`. Mechanical, 1:1 rewrite — each test's assertions on `response.Success`/`response.Registered`/`_registry.Get(...)`/thrown `RpcException` carry over unchanged since `RegisterAsync` returns the same `IReadOnlyList<string>` the gRPC method used to wrap into `SchemaResponse.Registered`, and every thrown exception is still `RpcException` with the same `StatusCode`. Worked example:

```csharp
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class SchemaRegistrationOrchestratorTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IRecordStoreSchemaManager _schemaManager = Substitute.For<IRecordStoreSchemaManager>();
    private readonly IVectorSchemaManager _vector = Substitute.For<IVectorSchemaManager>();
    private readonly IEngagementStoreSchemaManager _starRocks = Substitute.For<IEngagementStoreSchemaManager>();
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly SchemaRegistry _registry;
    private readonly SchemaRegistrationOrchestrator _sut;

    public SchemaRegistrationOrchestratorTests()
    {
        _embedding.Dimension.Returns(768);
        _embedding.ModelId.Returns("nomic-embed-text");
        _starRocks.ApplyTableAsync(Arg.Any<StarRocksTableSchema>()).Returns(Task.CompletedTask);
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new SchemaRegistrationOrchestrator(
            _schemaManager, _vector, _starRocks, _embedding, _registry,
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
    }

    private static TypeDescriptor SimpleType(string name, params string[] extraScalars)
    {
        var td = new TypeDescriptor { TypeName = name };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        foreach (var s in extraScalars)
            td.Properties.Add(new PropertyDescriptor { Name = s, ClrType = ClrType.ClrString });
        return td;
    }

    [Fact]
    public async Task RegisterAsync_WithInvalidOwnerField_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Name");
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "DoesNotExist" };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ... the remaining 13 tests follow the same 1:1 rewrite: same body, same assertions,
    // `_sut.RegisterAsync(request, CancellationToken.None)` in place of
    // `_sut.RegisterSchema(request, TestServerCallContext.Create())`, dropping the
    // `response.Success.Should().BeTrue()` assertion where the original test only checked
    // Success (RegisterAsync returns the registered-names list directly, no wrapping
    // Success/Error fields exist at this layer) and keeping `response.Should().Contain(...)`-style
    // assertions against the returned `IReadOnlyList<string>` unchanged.
}
```

Copy the `SimpleType` test helper (used across most of these 14 tests) from `ObjectMappingGrpcServiceTests.cs` into this new file verbatim.

- [ ] **Step 5: Update `ObjectMappingGrpcServiceTests.cs`**

Remove the 14 moved test bodies. Keep `RegisterSchema_WithNullRootType_ThrowsInvalidArgument` unchanged (its behavior stays in the gRPC method). This task removes 4 fields (`_schemaManager`, `_vector`, `_events`... — `_events` was already removed in Task 2 — `_starRocks`, `_embedding` become unused as `_sut` constructor arguments once `_schemaRegistration` replaces them; keep the fields themselves, since the new thin-check test and the `_schemaRegistration`-construction line below still use them). Add a new field:
```csharp
private readonly ISchemaRegistrationOrchestrator _schemaRegistration;
```
In the constructor, before `_sut = new ObjectMappingGrpcService(...)`:
```csharp
_schemaRegistration = new SchemaRegistrationOrchestrator(
    _schemaManager, _vector, _starRocks, _embedding, _registry, NullLogger<SchemaRegistrationOrchestrator>.Instance);
```
`_sut`'s constructor call becomes (matching the final 12-parameter shape from Step 2):
```csharp
_sut = new ObjectMappingGrpcService(
    _entities, _txRunner, _outboxPublisher, _registry,
    new RelationValidator(_registry), new EntityKeyAccessor(),
    new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
    NullLogger<ObjectMappingGrpcService>.Instance,
    _actingUserAccessor, _authEvaluator, _relationResolver, _schemaRegistration);
```
This is a **real** orchestrator instance, matching the same "shared `_sut` keeps real collaborators" pattern used in Tasks 2 and 3, since no other existing test needs to mock schema registration.

Add one new thin test verifying `RegisterSchema` correctly wraps the orchestrator's result, using a locally-constructed `sut` with a mocked `ISchemaRegistrationOrchestrator` (reusing the class's real `_outboxPublisher`/`_relationResolver` fields for the other two collaborator slots):

```csharp
[Fact]
public async Task RegisterSchema_ReturnsOrchestratorResult()
{
    var mockOrchestrator = Substitute.For<ISchemaRegistrationOrchestrator>();
    mockOrchestrator.RegisterAsync(Arg.Any<SchemaRequest>(), Arg.Any<CancellationToken>())
        .Returns(new List<string> { "Widget" });
    var sut = new ObjectMappingGrpcService(
        _entities, _txRunner, _outboxPublisher, _registry,
        new RelationValidator(_registry), new EntityKeyAccessor(),
        new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
        NullLogger<ObjectMappingGrpcService>.Instance,
        _actingUserAccessor, _authEvaluator, _relationResolver, mockOrchestrator);

    var response = await sut.RegisterSchema(
        new SchemaRequest { RootType = SimpleType("Widget", "Name") }, TestServerCallContext.Create());

    response.Success.Should().BeTrue();
    response.Registered.Should().BeEquivalentTo(new[] { "Widget" });
}
```

- [ ] **Step 6: Build, test, commit**

```bash
cd Iverson.Server
dotnet build Iverson.Api.Tests/Iverson.Api.Tests.csproj
dotnet test Iverson.Api.Tests/Iverson.Api.Tests.csproj --filter "FullyQualifiedName~SchemaRegistrationOrchestratorTests|FullyQualifiedName~ObjectMappingGrpcServiceTests"
git add Iverson.Server/Iverson.Api/Grpc/SchemaRegistrationOrchestrator.cs Iverson.Server/Iverson.Api/Grpc/ObjectMappingGrpcService.cs Iverson.Server/Iverson.Api/Program.cs Iverson.Server/Iverson.Api.Tests/Grpc/SchemaRegistrationOrchestratorTests.cs Iverson.Server/Iverson.Api.Tests/Grpc/ObjectMappingGrpcServiceTests.cs
git commit -m "refactor(api): extract SchemaRegistrationOrchestrator from ObjectMappingGrpcService"
```

---

## Tasks NOT in this plan

- Finding #5 (the `ProtoKindToSr`/`SrKindToProto` parallel-switch OCP cleanup in `ObjectSearchGrpcService`) — cosmetic, no defect history, excluded from this design at the user's direction.
