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
            _schemaManager, _vector, _starRocks, _embedding, _registry);
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

    [Fact]
    public async Task RegisterAsync_WithInvalidOwnerField_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Name");
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "DoesNotExist" };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithMissingTenantField_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Name", ClrType = ClrType.ClrString });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithInvalidTenantField_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget", TenantField = "DoesNotExist" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Name", ClrType = ClrType.ClrString });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithValidTenantField_Registers()
    {
        var td = SimpleType("Widget", "Name");

        var registered = await _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        registered.Should().Contain("Widget");
        _registry.Get("Widget")!.TenantColumn.Should().Be("TenantId");
    }

    [Fact]
    public async Task RegisterAsync_WithNonStringOwnerFieldSqlType_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Count", ClrType = ClrType.ClrInt32 });
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "Count" };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithOwnerFieldCollidingWithReservedChunkPayloadKey_ThrowsInvalidArgument()
    {
        var td = new TypeDescriptor { TypeName = "Widget" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Text", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor
            { Name = "Body", ClrType = ClrType.ClrString, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "Text" }; // "Text".ToCamelCase() == "text"

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithGuidTypedOwnerField_DoesNotThrow()
    {
        var td = new TypeDescriptor { TypeName = "Widget", TenantField = "TenantId" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "OwnerId", ClrType = ClrType.ClrGuid });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "OwnerId" };

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_CallsApplyTableAsync_WithMatchingTableName()
    {
        var request = new SchemaRequest { RootType = SimpleType("Author", "Name") };

        await _sut.RegisterAsync(request, CancellationToken.None);

        await _starRocks.Received(1).ApplyTableAsync(
            Arg.Is<StarRocksTableSchema>(s => s.TableName == "authors"));
    }

    [Fact]
    public async Task RegisterAsync_WithSimpleEntity_ReturnsSuccessAndPersistsInRegistry()
    {
        var request = new SchemaRequest { RootType = SimpleType("Tag", "Label") };

        var registered = await _sut.RegisterAsync(request, CancellationToken.None);

        registered.Should().Contain("Tag");
        _registry.Get("Tag").Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_WithInjectionRelevantTypeName_ThrowsInvalidArgument()
    {
        var request = new SchemaRequest
        {
            RootType = SimpleType("Foo\"; DROP TABLE x; --", "Name")
        };

        var act = () => _sut.RegisterAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithInjectionRelevantPropertyName_ThrowsInvalidArgument()
    {
        var request = new SchemaRequest
        {
            RootType = SimpleType("Widget", "Name\"; DROP TABLE x; --")
        };

        var act = () => _sut.RegisterAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithUnderscoreInTypeName_ThrowsInvalidArgument()
    {
        // Underscores aren't SQL-injection-relevant, but they're excluded per the allow-list
        // design: ToSnakeCase already inserts its own underscores, so accepting caller-supplied
        // ones would let a caller collide with or otherwise manipulate the generated identifier.
        var request = new SchemaRequest
        {
            RootType = SimpleType("Foo_Bar", "Name")
        };

        var act = () => _sut.RegisterAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterAsync_WithNormalAlphanumericNames_DoesNotThrow()
    {
        var request = new SchemaRequest { RootType = SimpleType("Widget2", "Name2") };

        var registered = await _sut.RegisterAsync(request, CancellationToken.None);

        registered.Should().Contain("Widget2");
    }

    [Fact]
    public async Task RegisterAsync_WithManyToOneRelation_DoesNotThrow()
    {
        var td = SimpleType("Comment", "Body", "ArticleId");
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Article",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "Article",
            ForeignKey   = "ArticleId"
        });

        var registered = await _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        registered.Should().Contain("Comment");
    }

    [Fact]
    public async Task RegisterAsync_WithManyToManyRelation_DoesNotThrow()
    {
        var td = new TypeDescriptor { TypeName = "Post", TenantField = "TenantId" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id",       ClrType = ClrType.ClrGuid,   IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TenantId", ClrType = ClrType.ClrString });
        td.Properties.Add(new PropertyDescriptor { Name = "TagIds",   ClrType = ClrType.ClrGuid,   IsArray = true });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tags",
            Kind         = Client.Contracts.RelationKind.ManyToMany,
            RelatedType  = "Tag",
            ForeignKey   = "TagIds"
        });

        var act = () => _sut.RegisterAsync(new SchemaRequest { RootType = td }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_WithDependents_RegistersAllTypes()
    {
        var request = new SchemaRequest
        {
            RootType = SimpleType("Article", "Title"),
            Dependents = { SimpleType("Author", "Name") }
        };

        var registered = await _sut.RegisterAsync(request, CancellationToken.None);

        registered.Should().Contain("Article").And.Contain("Author");
    }

    [Fact]
    public async Task RegisterAsync_SetsVectorDimAndModelId_FromEmbeddingService()
    {
        var typeDesc = new TypeDescriptor { TypeName = "EmbeddableDoc", TenantField = "TenantId" };
        typeDesc.Properties.Add(new PropertyDescriptor
        {
            Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true
        });
        typeDesc.Properties.Add(new PropertyDescriptor
        {
            Name = "TenantId", ClrType = ClrType.ClrString
        });
        typeDesc.Properties.Add(new PropertyDescriptor
        {
            Name    = "Content",
            ClrType = ClrType.ClrString,
            IsEmbedding = true,
            VectorDim   = 0,
            ModelId     = string.Empty
        });

        var request  = new SchemaRequest { RootType = typeDesc };
        await _sut.RegisterAsync(request, CancellationToken.None);

        var schema = _registry.Get("EmbeddableDoc")!;
        schema.VectorFields.Should().ContainSingle();
        schema.VectorFields[0].Dimension.Should().Be(768);
        schema.VectorFields[0].ModelId.Should().Be("nomic-embed-text");
    }
}
