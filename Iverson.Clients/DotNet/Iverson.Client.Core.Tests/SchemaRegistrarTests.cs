using System.Reflection;
using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ContractsRelKind = Iverson.Client.Contracts.RelationKind;

namespace Iverson.Client.Core.Tests;

// ── Test entity fixtures ───────────────────────────────────────────────────────
// Defined here so EntityRegistry only scans this assembly in tests.

[IversonEntity]
internal sealed class SearchAnnotationTestEntity
{
    [IversonKey]          public Guid            Id          { get; set; }
    [IversonTenant]        public string          TenantId    { get; set; } = string.Empty;
    [IversonSearchKey(0)] public string          Category    { get; set; } = "";
    [IversonSearchKey(1)] public DateTimeOffset  PublishedAt { get; set; }
    [IversonLargeField]   public string          Body        { get; set; } = "";
}

[IversonEntity]
[IversonDescription("Documents authored by a user.")]
internal sealed class MetadataAnnotationTestEntity
{
    [IversonKey]
    [IversonDescription("Primary identifier.")]
    public Guid Id { get; set; }

    [IversonTenant]
    public string TenantId { get; set; } = string.Empty;

    [IversonMetadata]
    [IversonDescription("Source system name.")]
    public string Source { get; set; } = "";

    [IversonMetadata]
    public string Region { get; set; } = "";

    public string Plain { get; set; } = "";
}

[IversonEntity]
internal sealed class EnrichmentAnnotationTestEntity
{
    [IversonKey]
    public Guid Id { get; set; }

    [IversonTenant]
    public string TenantId { get; set; } = string.Empty;

    [IversonSummary]
    public string Summary { get; set; } = "";

    [IversonKeywords]
    public string Keywords { get; set; } = "";

    [IversonExtracted("Extract the invoice total.")]
    public string ExtractedField { get; set; } = "";

    [IversonChunk(Contextual = true)]
    public string ContextualChunk { get; set; } = "";

    public string Plain { get; set; } = "";
}

[IversonEntity]
internal sealed class SchemaTestAuthor
{
    [IversonKey]
    public Guid Id { get; set; }
    [IversonTenant]
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Bio { get; set; }
}

[IversonEntity]
internal sealed class SchemaTestArticle
{
    [IversonKey]
    public Guid Id { get; set; }
    [IversonTenant]
    public string TenantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public Guid AuthorId { get; set; }

    [IversonEmbedding]
    public string Body { get; set; } = string.Empty;

    [ManyToOne(typeof(SchemaTestAuthor))]
    public SchemaTestAuthor? Author { get; set; }

    [OneToMany(typeof(SchemaTestTag))]
    public List<SchemaTestTag> Tags { get; set; } = [];
}

[IversonEntity]
internal sealed class SchemaTestTag
{
    [IversonKey]
    public Guid Id { get; set; }
    [IversonTenant]
    public string TenantId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public Guid ArticleId { get; set; }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class SchemaRegistrarTests
{
    private readonly ObjectMappingService.ObjectMappingServiceClient _mappingClient;
    private readonly EntityRegistry _registry;
    private readonly SchemaRegistrar _sut;

    public SchemaRegistrarTests()
    {
        _mappingClient = Substitute.For<ObjectMappingService.ObjectMappingServiceClient>();
        _registry = new EntityRegistry([typeof(SchemaTestAuthor).Assembly]);
        _sut = new SchemaRegistrar(_registry, _mappingClient,
            NullLogger<SchemaRegistrar>.Instance);

        SetupSuccessResponse();
    }

    private void SetupSuccessResponse(SchemaResponse? response = null)
    {
        var resp = response ?? new SchemaResponse { Success = true };
        var fakeCall = new AsyncUnaryCall<SchemaResponse>(
            Task.FromResult(resp),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

        _mappingClient
            .RegisterSchemaAsync(
                Arg.Any<SchemaRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(fakeCall);
    }

    // ── RegisterAllAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterAllAsync_CallsRegisterSchema_ForEachEntityType()
    {
        await _sut.RegisterAllAsync();

        var entityCount = _registry.All.Count();
        // Received() is synchronous — awaiting the proxy return value (AsyncUnaryCall) would NPE
        _ = _mappingClient.Received(entityCount)
            .RegisterSchemaAsync(
                Arg.Any<SchemaRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAllAsync_SendsCorrectTypeName_ForEachEntity()
    {
        var requests = new List<SchemaRequest>();
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r => requests.Add(r)),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        var typeNames = requests.Select(r => r.RootType!.TypeName).ToList();
        typeNames.Should().Contain("SchemaTestAuthor");
        typeNames.Should().Contain("SchemaTestArticle");
        typeNames.Should().Contain("SchemaTestTag");
    }

    [Fact]
    public async Task RegisterAllAsync_MarksKeyProperty_WithIsKeyTrue()
    {
        SchemaRequest? authorRequest = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SchemaTestAuthor") authorRequest = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        authorRequest.Should().NotBeNull();
        var keyProp = authorRequest!.RootType!.Properties.Single(p => p.IsKey);
        keyProp.Name.Should().Be("Id");
        keyProp.ClrType.Should().Be(ClrType.ClrGuid);
    }

    [Fact]
    public async Task RegisterAllAsync_SkipsNavigationProperties_FromScalarList()
    {
        SchemaRequest? articleRequest = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SchemaTestArticle") articleRequest = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        articleRequest.Should().NotBeNull();
        var propNames = articleRequest!.RootType!.Properties.Select(p => p.Name).ToList();

        // Nav properties must not appear as scalars
        propNames.Should().NotContain("Author");
        propNames.Should().NotContain("Tags");

        // Scalar FK should be included
        propNames.Should().Contain("AuthorId");
    }

    [Fact]
    public async Task RegisterAllAsync_AppliesEmbeddingAnnotation_OnMarkedProperty()
    {
        SchemaRequest? articleRequest = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SchemaTestArticle") articleRequest = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        articleRequest.Should().NotBeNull();
        var bodyProp = articleRequest!.RootType!.Properties.SingleOrDefault(p => p.Name == "Body");
        bodyProp.Should().NotBeNull();
        bodyProp!.IsEmbedding.Should().BeTrue();
        bodyProp.VectorDim.Should().Be(0);
        bodyProp.ModelId.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterAllAsync_BuildsRelations_WithInferredForeignKeys()
    {
        SchemaRequest? articleRequest = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SchemaTestArticle") articleRequest = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        articleRequest.Should().NotBeNull();
        var relations = articleRequest!.RootType!.Relations;

        var manyToOne = relations.Single(r => r.Kind == ContractsRelKind.ManyToOne);
        manyToOne.PropertyName.Should().Be("Author");
        manyToOne.RelatedType.Should().Be("SchemaTestAuthor");
        manyToOne.ForeignKey.Should().Be("SchemaTestAuthorId");

        var oneToMany = relations.Single(r => r.Kind == ContractsRelKind.OneToMany);
        oneToMany.PropertyName.Should().Be("Tags");
        oneToMany.RelatedType.Should().Be("SchemaTestTag");
        oneToMany.ForeignKey.Should().Be("SchemaTestArticleId");
    }

    [Fact]
    public async Task RegisterAllAsync_NullablePrimitives_AreMarkedNullable()
    {
        SchemaRequest? authorRequest = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SchemaTestAuthor") authorRequest = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        authorRequest.Should().NotBeNull();
        var bioProp = authorRequest!.RootType!.Properties.SingleOrDefault(p => p.Name == "Bio");
        bioProp.Should().NotBeNull();
        bioProp!.IsNullable.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAllAsync_SetsIsSearchKey_AndSearchKeyOrder_OnAnnotatedProperties()
    {
        SchemaRequest? req = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SearchAnnotationTestEntity") req = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        req.Should().NotBeNull();
        var category    = req!.RootType!.Properties.Single(p => p.Name == "Category");
        var publishedAt = req!.RootType!.Properties.Single(p => p.Name == "PublishedAt");
        category.IsSearchKey.Should().BeTrue();
        category.SearchKeyOrder.Should().Be(0);
        publishedAt.IsSearchKey.Should().BeTrue();
        publishedAt.SearchKeyOrder.Should().Be(1);
    }

    [Fact]
    public async Task RegisterAllAsync_SetsIsMetadata_AndDescriptions_OnAnnotatedMembers()
    {
        SchemaRequest? req = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "MetadataAnnotationTestEntity") req = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        req.Should().NotBeNull();
        req!.RootType!.Description.Should().Be("Documents authored by a user.");

        var id     = req.RootType.Properties.Single(p => p.Name == "Id");
        var source = req.RootType.Properties.Single(p => p.Name == "Source");
        var region = req.RootType.Properties.Single(p => p.Name == "Region");
        var plain  = req.RootType.Properties.Single(p => p.Name == "Plain");

        id.Description.Should().Be("Primary identifier.");
        id.IsMetadata.Should().BeFalse();

        source.IsMetadata.Should().BeTrue();
        source.Description.Should().Be("Source system name.");

        region.IsMetadata.Should().BeTrue();
        region.Description.Should().BeEmpty();

        plain.IsMetadata.Should().BeFalse();
        plain.Description.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterAllAsync_SetsIsLargeField_OnAnnotatedProperty()
    {
        SchemaRequest? req = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SearchAnnotationTestEntity") req = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        req.Should().NotBeNull();
        var body = req!.RootType!.Properties.Single(p => p.Name == "Body");
        body.IsLargeField.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAllAsync_WhenServerThrowsRpcException_PropagatesException()
    {
        var fakeError = new AsyncUnaryCall<SchemaResponse>(
            Task.FromException<SchemaResponse>(
                new RpcException(new Status(StatusCode.Unavailable, "server down"))),
            Task.FromResult(new Metadata()),
            () => new Status(StatusCode.Unavailable, "server down"),
            () => new Metadata(),
            () => { });

        _mappingClient
            .RegisterSchemaAsync(
                Arg.Any<SchemaRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(fakeError);

        var act = () => _sut.RegisterAllAsync();

        await act.Should().ThrowAsync<RpcException>()
            .Where(ex => ex.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task RegisterAllAsync_SetsEnrichmentAnnotations_OnAnnotatedMembers()
    {
        SchemaRequest? req = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "EnrichmentAnnotationTestEntity") req = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        req.Should().NotBeNull();
        var summary   = req!.RootType!.Properties.Single(p => p.Name == "Summary");
        var keywords  = req.RootType.Properties.Single(p => p.Name == "Keywords");
        var extracted = req.RootType.Properties.Single(p => p.Name == "ExtractedField");
        var chunk     = req.RootType.Properties.Single(p => p.Name == "ContextualChunk");
        var plain     = req.RootType.Properties.Single(p => p.Name == "Plain");

        summary.IsSummaryTarget.Should().BeTrue();

        keywords.IsKeywordsTarget.Should().BeTrue();

        extracted.ExtractHint.Should().Be("Extract the invoice total.");

        chunk.IsChunk.Should().BeTrue();
        chunk.ChunkContextual.Should().BeTrue();

        plain.IsSummaryTarget.Should().BeFalse();
        plain.IsKeywordsTarget.Should().BeFalse();
        plain.ExtractHint.Should().BeEmpty();
        plain.ChunkContextual.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void IversonExtractedAttribute_Throws_WhenHintIsBlank(string? hint)
    {
        var act = () => new IversonExtractedAttribute(hint!);

        act.Should().Throw<ArgumentException>()
           .WithMessage("*non-blank extraction hint*")
           .And.ParamName.Should().Be("hint");
    }

    [Fact]
    public void IversonExtractedAttribute_KeepsHint_WhenNonBlank()
    {
        new IversonExtractedAttribute("Extract the invoice total.")
            .Hint.Should().Be("Extract the invoice total.");
    }

    [Fact]
    public async Task RegisterAllAsync_SetsAuthorization_WhenSupplementProvidesEntry()
    {
        SchemaRequest? authorRequest = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SchemaTestAuthor") authorRequest = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var rules = new AuthorizationRules
        {
            OwnerField = "OwnerId",
            RowPermissions = { new RowPermission { Role = "test-bypass", CanReadAll = true } },
        };

        await _sut.RegisterAllAsync(
            authorizationByTypeName: new Dictionary<string, AuthorizationRules> { ["SchemaTestAuthor"] = rules });

        authorRequest.Should().NotBeNull();
        authorRequest!.RootType!.Authorization.Should().NotBeNull();
        authorRequest.RootType.Authorization.OwnerField.Should().Be("OwnerId");
        authorRequest.RootType.Authorization.RowPermissions.Single().Role.Should().Be("test-bypass");
    }

    [Fact]
    public async Task RegisterAllAsync_LeavesAuthorizationUnset_WhenSupplementHasNoEntryForType()
    {
        SchemaRequest? tagRequest = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SchemaTestTag") tagRequest = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync(
            authorizationByTypeName: new Dictionary<string, AuthorizationRules> { ["SchemaTestAuthor"] = new() });

        tagRequest.Should().NotBeNull();
        tagRequest!.RootType!.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAllAsync_SetsTenantField_FromMarkedProperty()
    {
        SchemaRequest? authorRequest = null;
        _mappingClient
            .RegisterSchemaAsync(
                Arg.Do<SchemaRequest>(r =>
                {
                    if (r.RootType?.TypeName == "SchemaTestAuthor") authorRequest = r;
                }),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<SchemaResponse>(
                Task.FromResult(new SchemaResponse { Success = true }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        await _sut.RegisterAllAsync();

        authorRequest.Should().NotBeNull();
        authorRequest!.RootType!.TenantField.Should().Be("TenantId");
    }

    private sealed class ComposedTenantMarkerEntity
    {
        public Guid Id { get; set; }
        [IversonTenant, IversonSearchKey(0)] public string TenantId { get; set; } = "";
    }

    [Fact]
    public void BuildTypeDescriptor_TenantMarker_ComposesWithSearchKey()
    {
        var method = typeof(SchemaRegistrar).GetMethod(
            "BuildTypeDescriptor", BindingFlags.NonPublic | BindingFlags.Static)!;

        var typeDescriptor = (TypeDescriptor)method.Invoke(
            null, [BuildDescriptor<ComposedTenantMarkerEntity>()])!;

        typeDescriptor.TenantField.Should().Be("TenantId");
        var tenant = typeDescriptor.Properties.Single(p => p.Name == "TenantId");
        tenant.IsSearchKey.Should().BeTrue(
            "[IversonTenant] must not suppress [IversonSearchKey]");
        tenant.SearchKeyOrder.Should().Be(0);
    }

    private sealed class NoTenantMarkerEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class DualTenantMarkerEntity
    {
        public Guid Id { get; set; }
        [IversonTenant] public string TenantA { get; set; } = "";
        [IversonTenant] public string TenantB { get; set; } = "";
    }

    private static EntityDescriptor BuildDescriptor<T>() where T : class =>
        new()
        {
            EntityType  = typeof(T),
            EntityName  = typeof(T).Name,
            KeyProperty = typeof(T).GetProperty(nameof(NoTenantMarkerEntity.Id))!,
            Relations   = []
        };

    private static Exception? InvokeBuildTypeDescriptor(EntityDescriptor descriptor)
    {
        var method = typeof(SchemaRegistrar).GetMethod(
            "BuildTypeDescriptor", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            method.Invoke(null, [descriptor]);
            return null;
        }
        catch (TargetInvocationException tie)
        {
            return tie.InnerException;
        }
    }

    [Fact]
    public void BuildTypeDescriptor_Throws_WhenNoPropertyIsMarkedTenant()
    {
        var ex = InvokeBuildTypeDescriptor(BuildDescriptor<NoTenantMarkerEntity>());

        ex.Should().BeOfType<ArgumentException>();
        ex!.Message.Should().Contain(nameof(NoTenantMarkerEntity));
    }

    [Fact]
    public void BuildTypeDescriptor_Throws_WhenMultiplePropertiesAreMarkedTenant()
    {
        var ex = InvokeBuildTypeDescriptor(BuildDescriptor<DualTenantMarkerEntity>());

        ex.Should().BeOfType<ArgumentException>();
        ex!.Message.Should().Contain("TenantA");
        ex.Message.Should().Contain("TenantB");
    }
}
