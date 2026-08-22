using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public class DocumentTemplateValidationTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IRecordStoreSchemaManager _schemaManager = Substitute.For<IRecordStoreSchemaManager>();
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly IDocumentRerenderQueueRepository _rerenderQueue = Substitute.For<IDocumentRerenderQueueRepository>();
    private readonly SchemaRegistry _registry;
    private readonly SchemaRegistrationOrchestrator _sut;

    public DocumentTemplateValidationTests()
    {
        _embedding.Dimension.Returns(768);
        _embedding.ModelId.Returns("nomic-embed-text");
        _registry = new SchemaRegistry(
            new SchemaRegistryRepository(_sql),
            NullLogger<SchemaRegistry>.Instance);
        _sut = new SchemaRegistrationOrchestrator(
            _schemaManager,
            _embedding,
            _registry,
            _rerenderQueue,
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
    }

    // Widget: Id, TenantId, Name (string), and a document template referencing {Name}.
    private static TypeDescriptor WidgetType(string documentTemplate = "{Name}")
    {
        var td = new TypeDescriptor
        {
            TypeName = "Widget", TenantField = "TenantId", DocumentTemplate = documentTemplate
        };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor { Name = "Name", ClrType = ClrType.ClrString });
        return td;
    }

    private static TypeDescriptor SimpleType(string name, params string[] extraScalars)
    {
        var td = new TypeDescriptor { TypeName = name, TenantField = "TenantId" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        foreach (var s in extraScalars)
            td.Properties.Add(new PropertyDescriptor { Name = s, ClrType = ClrType.ClrString });
        return td;
    }

    // ── Rule 1: undeclared property ─────────────────────────────────────────────

    [Fact]
    public async Task RegisterAsync_TemplateReferencesUndeclaredProperty_ThrowsInvalidArgument()
    {
        var td = WidgetType("{Bio}"); // Bio is not a declared property

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 2: undeclared relation ─────────────────────────────────────────────

    [Fact]
    public async Task RegisterAsync_TemplateReferencesUndeclaredRelation_ThrowsInvalidArgument()
    {
        var td = WidgetType("{Owner.Name}"); // no relation named "Owner" declared

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 3: scalar not declared on the target type ──────────────────────────

    [Fact]
    public async Task RegisterAsync_OneHopScalarNotOnTargetType_ThrowsInvalidArgument()
    {
        var root = WidgetType("{Owner.Bio}"); // Owner (User) has no Bio property
        root.Properties.Add(new PropertyDescriptor { Name = "UserId", ClrType = ClrType.ClrGuid });
        root.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner", Kind = Client.Contracts.RelationKind.ManyToOne, RelatedType = "User", ForeignKey = "UserId"
        });

        var dependent = SimpleType("User", "Name"); // no Bio

        var act = () => _sut.RegisterAsync(
            new SchemaRequest { RootType = root, Dependents = { dependent } }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 4: {Rel.Prop} on a collection relation ─────────────────────────────

    [Fact]
    public async Task RegisterAsync_OneHopOnCollectionRelation_ThrowsInvalidArgument()
    {
        var root = WidgetType("{Children.Name}");
        root.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Children", Kind = Client.Contracts.RelationKind.OneToMany, RelatedType = "Gadget", ForeignKey = "WidgetId"
        });

        var dependent = SimpleType("Gadget", "Name");

        var act = () => _sut.RegisterAsync(
            new SchemaRequest { RootType = root, Dependents = { dependent } }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 5: {#Rel} on a single-valued relation ──────────────────────────────

    [Fact]
    public async Task RegisterAsync_BlockOnSingleValuedRelation_ThrowsInvalidArgument()
    {
        var root = WidgetType("{#Owner}{Name}{/Owner}");
        root.Properties.Add(new PropertyDescriptor { Name = "UserId", ClrType = ClrType.ClrGuid });
        root.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner", Kind = Client.Contracts.RelationKind.ManyToOne, RelatedType = "User", ForeignKey = "UserId"
        });

        var dependent = SimpleType("User", "Name");

        var act = () => _sut.RegisterAsync(
            new SchemaRequest { RootType = root, Dependents = { dependent } }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 6: derived-vector-name collision, including a lowercase "document" property ──

    [Fact]
    public async Task RegisterAsync_LowercaseDocumentChunkProperty_CollidesWithSyntheticDocumentField_ThrowsInvalidArgument()
    {
        var td = WidgetType("{Name}");
        // A real [IversonChunk] property literally named "document" — ToSnakeCase("document") ==
        // ToSnakeCase("Document") == "document", so both derive the vector name "document_vector".
        td.Properties.Add(new PropertyDescriptor
        {
            Name = "document", ClrType = ClrType.ClrString, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 7: template references a FieldPermission-carrying property on the declaring type ──

    [Fact]
    public async Task RegisterAsync_TemplateReferencesFieldPermissionCarryingPropertyOnDeclaringType_ThrowsInvalidArgument()
    {
        var td = WidgetType("{Name}");
        td.Authorization = new Client.Contracts.AuthorizationRules
        {
            FieldPermissions =
            {
                new Client.Contracts.FieldPermission { FieldName = "Name", ReadableRoles = { "admin" } }
            }
        };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 8: the same, on a one-hop target ───────────────────────────────────

    [Fact]
    public async Task RegisterAsync_TemplateReferencesFieldPermissionCarryingPropertyOnOneHopTarget_ThrowsInvalidArgument()
    {
        var root = WidgetType("{Owner.Name}");
        root.Properties.Add(new PropertyDescriptor { Name = "UserId", ClrType = ClrType.ClrGuid });
        root.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner", Kind = Client.Contracts.RelationKind.ManyToOne, RelatedType = "User", ForeignKey = "UserId"
        });

        var dependent = SimpleType("User", "Name");
        dependent.Authorization = new Client.Contracts.AuthorizationRules
        {
            FieldPermissions =
            {
                new Client.Contracts.FieldPermission { FieldName = "Name", ReadableRoles = { "admin" } }
            }
        };

        var act = () => _sut.RegisterAsync(
            new SchemaRequest { RootType = root, Dependents = { dependent } }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 9: a FieldPermission naming "Document" ─────────────────────────────

    [Fact]
    public async Task RegisterAsync_FieldPermissionNamesDocument_ThrowsInvalidArgument()
    {
        var td = WidgetType("{Name}");
        td.Authorization = new Client.Contracts.AuthorizationRules
        {
            FieldPermissions =
            {
                new Client.Contracts.FieldPermission { FieldName = "Document", ReadableRoles = { "admin" } }
            }
        };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Rule 10: re-registering a target type without a property a dependent's template references ──

    [Fact]
    public async Task RegisterAsync_RetargetingTypeDropsPropertyADependentTemplateReferences_ThrowsFailedPrecondition()
    {
        // First: register User (with Bio) and Article (referencing {Author.Bio}) together —
        // this must succeed, establishing Article's template as valid against User.Bio.
        var user = SimpleType("User", "Name", "Bio");

        var article = SimpleType("Article");
        article.DocumentTemplate = "{Author.Bio}";
        article.Properties.Add(new PropertyDescriptor { Name = "UserId", ClrType = ClrType.ClrGuid });
        article.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Author", Kind = Client.Contracts.RelationKind.ManyToOne, RelatedType = "User", ForeignKey = "UserId"
        });

        await _sut.RegisterAsync(
            new SchemaRequest { RootType = article, Dependents = { user } }, CancellationToken.None);

        // Now: re-register User alone, without Bio. Article (already registered, not part of
        // this request) still references {Author.Bio} — this must be rejected, distinctly, as
        // FailedPrecondition rather than InvalidArgument.
        var userWithoutBio = SimpleType("User", "Name");

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = userWithoutBio }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    // ── Plus: root template referencing a type that appears later in dependents ────

    [Fact]
    public async Task RegisterAsync_RootTemplateReferencesLaterDependent_Succeeds()
    {
        var root = WidgetType("{Owner.Name}");
        root.Properties.Add(new PropertyDescriptor { Name = "UserId", ClrType = ClrType.ClrGuid });
        root.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner", Kind = Client.Contracts.RelationKind.ManyToOne, RelatedType = "User", ForeignKey = "UserId"
        });

        var dependent = SimpleType("User", "Name");

        var act = () => _sut.RegisterAsync(
            new SchemaRequest { RootType = root, Dependents = { dependent } }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Plus: SearchChunks(property: "document") succeeds despite an unrelated FieldPermission ──

    [Fact]
    public async Task SearchChunks_ForDocumentProperty_SucceedsDespiteUnrelatedFieldPermission()
    {
        // Registers directly against the registry (as ObjectSearchGrpcServiceTests does) rather
        // than through the orchestrator: this is a regression guard for the RowFieldAuthorizationEvaluator
        // companion rule, not for registration validation itself.
        var schema = new SchemaDescriptor
        {
            TypeName       = "Widget",
            TableName      = "widgets",
            CollectionName = "widgets",
            KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Name", "text", false), new ColumnDescriptor("Title", "text", false)],
            FkColumns      = [],
            VectorFields   = [],
            ChunkFields    = [new ChunkDescriptor("Document", 512, 64, "nomic-embed-text", 768)],
            Relations      = [],
            Authorization  = new Iverson.Api.Schema.AuthorizationRules(
                null,
                [new Iverson.Api.Schema.RowPermission("test-bypass", true, true, true)],
                [new Iverson.Api.Schema.FieldPermission("Title", ["admin"], [])]), // excludes Title, NOT Document
            TenantColumn   = "TenantId"
        };
        await _registry.RegisterAsync(schema);

        var vector = Substitute.For<IVectorQueryService>();
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[768]);
        vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Qdrant.Client.Grpc.Filter>())
              .Returns(new List<VectorSearchResult>().AsReadOnly());
        var search = Substitute.For<IEngagementStoreSearchService>();
        var actingUserAccessor = new ActingUserAccessor
            { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };

        var searchService = new ObjectSearchGrpcService(
            _registry, search, vector, embedding,
            NullLogger<ObjectSearchGrpcService>.Instance,
            actingUserAccessor, new RowFieldAuthorizationEvaluator(),
            new IntelligenceTenantScope("test-signing-key-0123456789abcdef"),
            new ResultReranker(), new ResultDiversifier());

        var writer = Substitute.For<IServerStreamWriter<ChunkSearchResponse>>();
        writer.WriteAsync(Arg.Any<ChunkSearchResponse>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var act = () => searchService.SearchChunks(
            new SearchChunksRequest { TypeName = "Widget", Property = "document", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        await act.Should().NotThrowAsync();
    }

    // ── Plus: a template-validation failure leaves the type unregistered ───────

    [Fact]
    public async Task RegisterAsync_TemplateValidationFailure_LeavesTypeUnregistered()
    {
        var td = WidgetType("{Bio}"); // Bio is undeclared -> InvalidArgument

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>();

        _registry.Get("Widget").Should().BeNull();
        await _schemaManager.DidNotReceive().ApplySchemaAsync(Arg.Any<TableSchema>(), Arg.Any<SchemaDriftPolicy>());
    }

    [Fact]
    public async Task RegisterAsync_TemplateValidationFailure_DoesNotOverwritePriorDescriptor()
    {
        // Register Widget successfully first...
        await _sut.RegisterAsync(new SchemaRequest { RootType = WidgetType("{Name}") }, CancellationToken.None);
        var priorDescriptor = _registry.Get("Widget");
        priorDescriptor.Should().NotBeNull();

        // ...then attempt to re-register it with a broken template.
        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = WidgetType("{Bio}") }, CancellationToken.None);

        await act.Should().ThrowAsync<RpcException>();

        _registry.Get("Widget").Should().BeEquivalentTo(priorDescriptor);
    }

    // ── Plus: parser-level exceptions surface as InvalidArgument, not Unknown ──

    [Fact]
    public async Task RegisterAsync_UnparseablePlaceholder_ThrowsInvalidArgumentNotUnknown()
    {
        var td = WidgetType("{Not a valid identifier}");

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_UnclosedBlock_ThrowsInvalidArgumentNotUnknown()
    {
        var td = WidgetType("{#Owner}{Name}"); // no closing {/Owner}
        td.Properties.Add(new PropertyDescriptor { Name = "UserId", ClrType = ClrType.ClrGuid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Owner", Kind = Client.Contracts.RelationKind.ManyToOne, RelatedType = "User", ForeignKey = "UserId"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── T9: backfill enqueue on template add/change ─────────────────────────────

    [Fact]
    public async Task RegisterAsync_UnchangedTemplate_EnqueuesNoBackfill()
    {
        await _sut.RegisterAsync(new SchemaRequest { RootType = WidgetType("{Name}") }, CancellationToken.None);
        _rerenderQueue.ClearReceivedCalls();

        // Re-registering with the exact same template text is routine (e.g. every service
        // restart re-running registration) and must not enqueue a type-level backfill row.
        await _sut.RegisterAsync(new SchemaRequest { RootType = WidgetType("{Name}") }, CancellationToken.None);

        await _rerenderQueue.DidNotReceive().EnqueueTypeAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task RegisterAsync_NewlyAddedTemplate_EnqueuesTypeLevelBackfill()
    {
        // First register Widget with no template at all (SimpleType leaves DocumentTemplate
        // unset, which protobuf defaults to "" — SchemaBuilder maps that to a null
        // DocumentTemplateSource)...
        var td = SimpleType("Widget", "Name");
        await _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);
        _rerenderQueue.ClearReceivedCalls();

        // ...then re-register it with a template for the first time.
        await _sut.RegisterAsync(new SchemaRequest { RootType = WidgetType("{Name}") }, CancellationToken.None);

        await _rerenderQueue.Received(1).EnqueueTypeAsync("Widget");
    }

    [Fact]
    public async Task RegisterAsync_EditedTemplate_EnqueuesTypeLevelBackfill()
    {
        await _sut.RegisterAsync(new SchemaRequest { RootType = WidgetType("{Name}") }, CancellationToken.None);
        _rerenderQueue.ClearReceivedCalls();

        var widget = SimpleType("Widget", "Name");
        widget.DocumentTemplate = "{Name} updated";

        await _sut.RegisterAsync(new SchemaRequest { RootType = widget }, CancellationToken.None);

        await _rerenderQueue.Received(1).EnqueueTypeAsync("Widget");
    }

    [Fact]
    public async Task RegisterAsync_TemplateRemoved_EnqueuesNoBackfill()
    {
        // final-review Finding 3: with the template gone, SchemaBuilder no longer emits the
        // synthetic "Document" chunk field, so nothing in ChunkFields can ever be used to clean
        // up the old chunk points — a type-level backfill here would just re-ingest the whole
        // type for nothing.
        await _sut.RegisterAsync(new SchemaRequest { RootType = WidgetType("{Name}") }, CancellationToken.None);
        _rerenderQueue.ClearReceivedCalls();

        var widget = SimpleType("Widget", "Name"); // DocumentTemplate unset -> null source

        await _sut.RegisterAsync(new SchemaRequest { RootType = widget }, CancellationToken.None);

        await _rerenderQueue.DidNotReceive().EnqueueTypeAsync(Arg.Any<string>());
    }
}
