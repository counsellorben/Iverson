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
internal sealed class SchemaTestAuthor
{
    [IversonKey]
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Bio { get; set; }
}

[IversonEntity]
internal sealed class SchemaTestArticle
{
    [IversonKey]
    public Guid Id { get; set; }
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
}
