using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class EngagementStoreConsumerTests
{
    private readonly IEventConsumer _consumer;
    private readonly IEngagementStoreEntityStore _sr;
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly IEntityRepository _entities;
    private readonly Api.Schema.SchemaRegistry _registry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public EngagementStoreConsumerTests()
    {
        _consumer = Substitute.For<IEventConsumer>();
        _sr       = Substitute.For<IEngagementStoreEntityStore>();
        _sql      = Substitute.For<IRecordStoreQueryExecutor>();
        _entities = Substitute.For<IEntityRepository>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _sr.UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>(), Arg.Any<string>())
           .Returns(Task.CompletedTask);
        _sr.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
           .Returns(Task.CompletedTask);
        _sr.EnsureTenantProvisionedAsync(Arg.Any<string>(), Arg.Any<StarRocksTableSchema>())
           .Returns(Task.CompletedTask);
        // Default: authoritative row agrees with the event payload's owner value used across
        // the pre-existing (non-adversarial) tests in this file, and carries the tenant value
        // used for mandatory tenant re-derivation (HandleUpsertAsync fetches this row before
        // ever reaching the owner-field logic, which is null for AuthorSchema()'s
        // BypassAuthorization()).
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"Name":"Alice","TenantId":"tenant-a"}""");

        _registry = new Api.Schema.SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<Api.Schema.SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private EngagementStoreConsumer BuildSut() =>
        new(_consumer, _sr, _registry, _entities, NullLogger<EngagementStoreConsumer>.Instance);

    [Fact]
    public async Task HandleUpsert_WithEngagementFlag_CallsUpsertAsync()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).UpsertAsync(
            Arg.Is<StarRocksTableSchema>(s => s.TableName == "authors"),
            Arg.Any<string>(),
            "tenant-a");
    }

    [Fact]
    public async Task HandleDelete_WithEngagementFlag_CallsDeleteAsync()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var key = Guid.NewGuid().ToString();

        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Author",
            Key:           key,
            PayloadJson:   """{"TenantId":"tenant-a"}""",
            TraceId:       "trace-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).DeleteAsync("authors", "Id", key, "tenant-a");
    }

    [Fact]
    public async Task SkipsEvent_WhenNoEngagementFlag()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.DidNotReceive().UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandlesMalformedJson_ThrowsPoisonMessageException()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var act = async () =>
            await BuildSut().HandleUpsertAsync("some-key", "NOT_VALID_JSON{{{", CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task DropsEvent_WhenSchemaNotRegistered()
    {
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Unknown",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   "{}",
            TraceId:       "trace-5",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.DidNotReceive().UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DispatchAsync_CreatedEvent_RoutesToUpsert()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-dispatch-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>(), Arg.Any<string>());
        await _sr.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DispatchAsync_UpdatedEvent_RoutesToUpsert()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var ev = new EntityEvent(
            EventType:     EntityEventType.Updated,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-dispatch-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DispatchAsync_DeletedEvent_RoutesToDelete()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var key = Guid.NewGuid().ToString();

        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Author",
            Key:           key,
            PayloadJson:   """{"TenantId":"tenant-a"}""",
            TraceId:       "trace-dispatch-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.Received(1).DeleteAsync("authors", "Id", key, "tenant-a");
        await _sr.DidNotReceive().UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleUpsert_WithForgedOwnerValueInPayload_UpsertsAuthoritativeValueNotPayloadValue()
    {
        // CSR #7 (StarRocks sibling) regression: a forged/stale event whose payload owner value
        // disagrees with the authoritative Postgres row must NOT propagate the payload's
        // (untrusted) value into StarRocks, since StarRocks's read-time row authorization
        // filters on this column's stored value.
        var schema = SchemaFixtures.AuthorSchema() with
        {
            Authorization = new AuthorizationRules(
                "Name",
                new List<RowPermission> { new("test-bypass", true, true, true) },
                new List<FieldPermission>())
        };
        await _registry.RegisterAsync(schema);

        const string forgedOwner = "Forged";
        const string realOwner   = "RealOwner";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns($$"""{"Name":"{{realOwner}}","TenantId":"tenant-a"}""");

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   $$"""{"Name":"{{forgedOwner}}"}""",
            TraceId:       "trace-forged-owner",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        string? capturedJson = null;
        _sr.UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Do<string>(j => capturedJson = j), Arg.Any<string>())
           .Returns(Task.CompletedTask);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedJson!);
        doc.RootElement.GetProperty("Name").GetString().Should().Be(realOwner);
    }

    [Fact]
    public async Task HandleUpsert_WithOwnerFieldAndNoAuthoritativeRow_OmitsOwnerKeyFromPayload()
    {
        // Fail-closed: if the authoritative row has no owner value (e.g. a delete-then-recreate
        // race), do NOT fall back to the event payload's unvalidated owner value — omit the key
        // entirely. The row still carries a TenantId here: since tenant re-derivation is now
        // mandatory and runs before the owner-field logic (via the same FetchByKeyAsync call),
        // a row missing TenantId entirely would drop the whole event before ever reaching the
        // owner-omission behavior this test targets.
        var schema = SchemaFixtures.AuthorSchema() with
        {
            Authorization = new AuthorizationRules(
                "Name",
                new List<RowPermission> { new("test-bypass", true, true, true) },
                new List<FieldPermission>())
        };
        await _registry.RegisterAsync(schema);

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"TenantId":"tenant-a"}""");

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-missing-row",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        string? capturedJson = null;
        _sr.UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Do<string>(j => capturedJson = j), Arg.Any<string>())
           .Returns(Task.CompletedTask);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedJson.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedJson!);
        doc.RootElement.TryGetProperty("Name", out _).Should().BeFalse();
    }

    [Fact]
    public async Task HandleUpsert_WithNoOwnerFieldConfigured_PassesPayloadUnchangedAndCallsFetchByKeyAsyncOnlyOnceForTenant()
    {
        // Efficiency/correctness: schemas without ownership (OwnerField == null) must not pay the
        // *extra* owner-value Postgres read, and must pass the event payload through byte-for-byte.
        // FetchByKeyAsync is still called exactly once — mandatory tenant re-derivation always
        // reads the authoritative row, independent of whether OwnerField is configured.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // BypassAuthorization() has OwnerField == null

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-no-owner-field",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        string? capturedJson = null;
        _sr.UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Do<string>(j => capturedJson = j), Arg.Any<string>())
           .Returns(Task.CompletedTask);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedJson.Should().Be(ev.PayloadJson);
        await _entities.Received(1).FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleUpsert_WithNoAuthoritativeTenantValue_SkipsProvisioningAndUpsert()
    {
        // Fail-closed: if the authoritative row carries no tenant value at all, the whole event
        // must be dropped before ever calling EnsureTenantProvisionedAsync or UpsertAsync — a
        // missing/forged tenant value must never provision or write to any tenant database.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"Name":"Alice"}""");

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-no-tenant",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await BuildSut().HandleUpsertAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _sr.DidNotReceive().EnsureTenantProvisionedAsync(Arg.Any<string>(), Arg.Any<StarRocksTableSchema>());
        await _sr.DidNotReceive().UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleUpsert_ForNewTenant_CallsEnsureTenantProvisionedAsyncOnce_ThenSkipsOnSecondEvent()
    {
        // Idempotency-cache behavior: two upsert events for the same (tenant, type) pair must only
        // provision the tenant database once — the second event must find the pair already in the
        // consumer's per-process _provisioned cache and skip straight to UpsertAsync.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var sut = BuildSut();

        var ev1 = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Alice"}""",
            TraceId:       "trace-provision-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);
        var ev2 = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Author",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Name":"Bob"}""",
            TraceId:       "trace-provision-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement);

        await sut.HandleUpsertAsync(ev1.Key, Serialize(ev1), CancellationToken.None);
        await sut.HandleUpsertAsync(ev2.Key, Serialize(ev2), CancellationToken.None);

        await _sr.Received(1).EnsureTenantProvisionedAsync(Arg.Any<string>(), Arg.Any<StarRocksTableSchema>());
        await _sr.Received(2).UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>(), Arg.Any<string>());
    }
}
