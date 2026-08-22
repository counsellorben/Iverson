using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class DocumentRerenderConsumerTests
{
    private readonly IEventConsumer _consumer = Substitute.For<IEventConsumer>();
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IDocumentRerenderQueueRepository _queue = Substitute.For<IDocumentRerenderQueueRepository>();
    private readonly SchemaRegistry _registry;

    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private static readonly string AuthorId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string WidgetId  = "22222222-0000-0000-0000-000000000001";
    private static readonly string WidgetId2 = "22222222-0000-0000-0000-000000000002";
    private static readonly string BadgeId   = "33333333-0000-0000-0000-000000000001";
    private static readonly string CommentId = "44444444-0000-0000-0000-000000000001";
    private static readonly string CategoryId = "55555555-0000-0000-0000-000000000001";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public DocumentRerenderConsumerTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private DocumentRerenderConsumer BuildSut() =>
        new(_consumer, _registry, _entities, _queue, NullLogger<DocumentRerenderConsumer>.Instance);

    // ── Schema fixtures ──────────────────────────────────────────────────────
    // Widget: ManyToOne Author (conventional FK "AuthorId"), ManyToOne Editor with a
    // deliberately non-conventional FK ("EditorRef"), OneToMany Comments (FK lives on
    // Comment.WidgetId), ManyToMany Categories (FK is Widget.CategoryIds, a uuid[]). All four
    // relations are referenced by the template, so all four appear in the reverse index.
    private static SchemaDescriptor WidgetSchema() => new()
    {
        TypeName      = "Widget",
        TableName     = "widgets",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("AuthorId",    "UUID",   true),
            new ColumnDescriptor("EditorRef",    "UUID",   true),
            new ColumnDescriptor("CategoryIds", "UUID[]", true),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    =
        [
            new RelationDescriptor("Author",     RelationKind.ManyToOne,  "Author",   "AuthorId"),
            new RelationDescriptor("Editor",     RelationKind.ManyToOne,  "Author",   "EditorRef"),
            new RelationDescriptor("Comments",   RelationKind.OneToMany,  "Comment",  "WidgetId"),
            new RelationDescriptor("Categories", RelationKind.ManyToMany, "Category", "CategoryIds"),
        ],
        TenantColumn           = "TenantId",
        DocumentTemplate       = DocumentTemplateParser.Parse(
            "{Author.Name} {Editor.Name} {#Comments}{Body}{/Comments} {#Categories}{Label}{/Categories}"),
        DocumentTemplateSource = "{Author.Name} {Editor.Name} {#Comments}{Body}{/Comments} {#Categories}{Label}{/Categories}",
    };

    // Badge: a OneToOne relation to Author (conventional FK "AuthorId") — covers the OneToOne
    // relation direction distinctly from Widget's ManyToOne.
    private static SchemaDescriptor BadgeSchema() => new()
    {
        TypeName      = "Badge",
        TableName     = "badges",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns = [new ColumnDescriptor("AuthorId", "UUID", true)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [new RelationDescriptor("Owner", RelationKind.OneToOne, "Author", "AuthorId")],
        TenantColumn           = "TenantId",
        DocumentTemplate       = DocumentTemplateParser.Parse("{Owner.Name}"),
        DocumentTemplateSource = "{Owner.Name}",
    };

    private static SchemaDescriptor AuthorSchema() => new()
    {
        TypeName      = "Author",
        TableName     = "authors",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns = [new ColumnDescriptor("Name", "TEXT", false)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
    };

    private static SchemaDescriptor CommentSchema() => new()
    {
        TypeName      = "Comment",
        TableName     = "comments",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns = [new ColumnDescriptor("Body", "TEXT", false), new ColumnDescriptor("WidgetId", "UUID", true)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
    };

    private static SchemaDescriptor CategorySchema() => new()
    {
        TypeName      = "Category",
        TableName     = "categories",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns = [new ColumnDescriptor("Label", "TEXT", false)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
    };

    private static EntityEvent MakeEvent(
        EntityEventType type, string typeName, string key, string payload,
        string? priorPayload = null, bool suppress = false) =>
        new(
            EventType:               type,
            TypeName:                typeName,
            Key:                     key,
            PayloadJson:             payload,
            TraceId:                 "trace-1",
            SchemaVersion:           "1",
            OccurredAt:              DateTimeOffset.UtcNow,
            TargetStores:            StoreTarget.All,
            PriorPayloadJson:        priorPayload,
            SuppressRerenderCascade: suppress);

    // ── ManyToOne direction ──────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_AuthorUpdated_EnqueuesWidgetViaManyToOneAuthorRelation()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), AuthorId)
            .Returns($$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), "AuthorId", AuthorId, true, TenantA)
            .Returns([$$"""{"Id":"{{WidgetId}}","TenantId":"{{TenantA}}"}"""]);
        // Editor relation also targets Author with a different FK column — must return nothing
        // so this test isolates the AuthorId path.
        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), "EditorRef", AuthorId, true, TenantA)
            .Returns([]);

        var ev = MakeEvent(EntityEventType.Updated, "Author", AuthorId,
            $$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId);
    }

    // ── OneToOne direction ───────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_AuthorUpdated_EnqueuesBadgeViaOneToOneOwnerRelation()
    {
        await _registry.RegisterAsync(BadgeSchema());
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), AuthorId)
            .Returns($$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), "AuthorId", AuthorId, true, TenantA)
            .Returns([$$"""{"Id":"{{BadgeId}}","TenantId":"{{TenantA}}"}"""]);

        var ev = MakeEvent(EntityEventType.Updated, "Author", AuthorId,
            $$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Badge", BadgeId);
    }

    // ── Explicit non-conventional foreignKey ────────────────────────────────

    [Fact]
    public async Task Dispatch_AuthorUpdated_EnqueuesWidgetViaNonConventionalEditorRefColumn()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), AuthorId)
            .Returns($$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), "AuthorId", AuthorId, true, TenantA)
            .Returns([]);
        // Only the non-conventional "EditorRef" column returns a match — proves the consumer
        // used relation.ForeignKey ("EditorRef") and not a "{TypeName}Id" convention-derived
        // column name ("AuthorId") for the Editor relation.
        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), "EditorRef", AuthorId, true, TenantA)
            .Returns([$$"""{"Id":"{{WidgetId}}","TenantId":"{{TenantA}}"}"""]);

        var ev = MakeEvent(EntityEventType.Updated, "Author", AuthorId,
            $$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId);
    }

    // ── ManyToMany direction (array containment) ────────────────────────────

    [Fact]
    public async Task Dispatch_CategoryUpdated_EnqueuesWidgetViaArrayContainment()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(CategorySchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CategoryId)
            .Returns($$"""{"Id":"{{CategoryId}}","Label":"Sale","TenantId":"{{TenantA}}"}""");

        _entities.FetchByArrayContainsAsync(Arg.Any<TableSchema>(), "CategoryIds", CategoryId, true, TenantA)
            .Returns([$$"""{"Id":"{{WidgetId}}","TenantId":"{{TenantA}}"}"""]);

        var ev = MakeEvent(EntityEventType.Updated, "Category", CategoryId,
            $$"""{"Id":"{{CategoryId}}","Label":"Sale","TenantId":"{{TenantA}}"}""");

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId);
    }

    // ── OneToMany direction (payload FK, not a query) ───────────────────────

    [Fact]
    public async Task Dispatch_CommentCreated_EnqueuesWidgetViaOneToManyPayloadForeignKey()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(CommentSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId)
            .Returns($$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId}}","TenantId":"{{TenantA}}"}""");

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId,
            $$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId}}","TenantId":"{{TenantA}}"}""");

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId);
        // No column/array query needed for OneToMany — the parent key comes straight from the
        // payload.
        await _entities.DidNotReceive().FetchByColumnAsync(
            Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    // ── Created / Updated / Deleted all trigger ─────────────────────────────

    [Theory]
    [InlineData(EntityEventType.Created)]
    [InlineData(EntityEventType.Updated)]
    [InlineData(EntityEventType.Deleted)]
    public async Task Dispatch_AllEventTypes_EnqueueDependent(EntityEventType eventType)
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(CommentSchema());

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId).Returns(payload);

        var ev = MakeEvent(eventType, "Comment", CommentId, payload);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId);
    }

    // ── FK reassignment enqueues BOTH parents ───────────────────────────────

    [Fact]
    public async Task Dispatch_CommentReparented_EnqueuesBothOldAndNewParent()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(CommentSchema());

        var newPayload = $$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId2}}","TenantId":"{{TenantA}}"}""";
        var priorPayload = $$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId}}","TenantId":"{{TenantA}}"}""";

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId).Returns(newPayload);

        var ev = MakeEvent(EntityEventType.Updated, "Comment", CommentId, newPayload, priorPayload: priorPayload);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId2); // new parent
        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId);  // old parent
    }

    [Fact]
    public async Task Dispatch_CommentReparented_DoesNotDoubleEnqueueWhenParentUnchanged()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(CommentSchema());

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi v2","WidgetId":"{{WidgetId}}","TenantId":"{{TenantA}}"}""";
        var priorPayload = $$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId}}","TenantId":"{{TenantA}}"}""";

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId).Returns(payload);

        var ev = MakeEvent(EntityEventType.Updated, "Comment", CommentId, payload, priorPayload: priorPayload);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId);
    }

    // ── SuppressRerenderCascade breaks the loop ─────────────────────────────

    [Fact]
    public async Task Dispatch_SuppressRerenderCascadeSet_DoesNothingAtAll()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(AuthorSchema());

        var ev = MakeEvent(EntityEventType.Updated, "Author", AuthorId,
            $$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""",
            suppress: true);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.DidNotReceiveWithAnyArgs().EnqueueEntityAsync(default, default!, default!);
        await _entities.DidNotReceiveWithAnyArgs().FetchByKeyAsync(default!, default!);
    }

    // ── Deleted event for a related entity uses the payload snapshot ───────

    [Fact]
    public async Task Dispatch_CategoryDeleted_FindsDependentsViaPayloadSnapshotNotAuthoritativeRow()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(CategorySchema());

        _entities.FetchByArrayContainsAsync(Arg.Any<TableSchema>(), "CategoryIds", CategoryId, true, TenantA)
            .Returns([$$"""{"Id":"{{WidgetId}}","TenantId":"{{TenantA}}"}"""]);

        var ev = MakeEvent(EntityEventType.Deleted, "Category", CategoryId,
            $$"""{"Id":"{{CategoryId}}","Label":"Sale","TenantId":"{{TenantA}}"}""");

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Widget", WidgetId);
        // The row is gone by the time a Deleted event is consumed — the tenant must have come
        // from the payload snapshot, not a re-fetch of a now-nonexistent authoritative row.
        await _entities.DidNotReceiveWithAnyArgs().FetchByKeyAsync(default!, default!);
    }

    // ── Reverse lookup does not cross a tenant boundary ─────────────────────

    [Fact]
    public async Task Dispatch_AuthorUpdated_DoesNotEnqueueDependentFromAnotherTenant()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), AuthorId)
            .Returns($$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        // The scoped lookup for TenantA returns nothing (simulating RLS: the only widget
        // referencing this author actually belongs to TenantB, so a TenantA-scoped query never
        // sees it).
        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), "AuthorId", AuthorId, true, TenantA)
            .Returns([]);
        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), "EditorRef", AuthorId, true, TenantA)
            .Returns([]);
        // A different-tenant call would return a row, proving the stub setup is meaningful —
        // but the consumer must call with TenantA (the changed Author's own tenant), never
        // TenantB, so this is never hit.
        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), "AuthorId", AuthorId, true, TenantB)
            .Returns([$$"""{"Id":"{{WidgetId}}","TenantId":"{{TenantB}}"}"""]);

        var ev = MakeEvent(EntityEventType.Updated, "Author", AuthorId,
            $$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.DidNotReceiveWithAnyArgs().EnqueueEntityAsync(default, default!, default!);
    }

    // ── Null tenant never reaches EnqueueEntityAsync ────────────────────────

    [Fact]
    public async Task Dispatch_ChangedEntityHasNoTenantColumn_EnqueuesNothing()
    {
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(CommentSchema() with { TenantColumn = null });

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","WidgetId":"{{WidgetId}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _queue.DidNotReceiveWithAnyArgs().EnqueueEntityAsync(default, default!, default!);
    }

    // ── Per-dependent error isolation (final-review Finding 6) ─────────────

    [Fact]
    public async Task Dispatch_OneDependentThrows_OtherDependentsAreStillEnqueued()
    {
        // This consumer has its own Kafka group, so an unhandled throw here would stall its
        // offset commit for the whole topic — one persistently failing dependent (a dropped
        // schema, a malformed FK column) must not starve re-render detection for every other
        // dependent of the same changed entity, let alone every other type on the topic.
        await _registry.RegisterAsync(WidgetSchema());
        await _registry.RegisterAsync(BadgeSchema());
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), AuthorId)
            .Returns($$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        // Widget's "Author" relation (FK "AuthorId" on widgets) throws — simulates a malformed
        // FK column or dropped schema for that one dependent.
        _entities.FetchByColumnAsync(
                Arg.Is<TableSchema>(s => s.TableName == "widgets"), "AuthorId", AuthorId, true, TenantA)
            .Returns<IEnumerable<string>>(_ => throw new InvalidOperationException("boom"));
        // Widget's "Editor" relation (FK "EditorRef") is unaffected.
        _entities.FetchByColumnAsync(
                Arg.Is<TableSchema>(s => s.TableName == "widgets"), "EditorRef", AuthorId, true, TenantA)
            .Returns([]);
        // Badge's "Owner" relation (FK "AuthorId" on badges) is unaffected.
        _entities.FetchByColumnAsync(
                Arg.Is<TableSchema>(s => s.TableName == "badges"), "AuthorId", AuthorId, true, TenantA)
            .Returns([$$"""{"Id":"{{BadgeId}}","TenantId":"{{TenantA}}"}"""]);

        var ev = MakeEvent(EntityEventType.Updated, "Author", AuthorId,
            $$"""{"Id":"{{AuthorId}}","Name":"Ada","TenantId":"{{TenantA}}"}""");

        var act = () => BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _queue.Received(1).EnqueueEntityAsync(TenantA, "Badge", BadgeId);
        await _queue.DidNotReceive().EnqueueEntityAsync(TenantA, "Widget", WidgetId);
    }
}
