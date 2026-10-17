using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public sealed class SchemaRegistryRoundTripPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// CSR round 12 §5 sends owner-stamped descriptors from the api to the worker serialised with the
/// registry's JSON options, and the worker writes them to <c>_iverson_schema</c>. That is only
/// sound if a descriptor survives the trip through Postgres whole, so this pins it: every member,
/// <see cref="SchemaDescriptor.OwnerTenantId"/> included, and the table the descriptor projects to.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class SchemaRegistryPostgresRoundTripTests(SchemaRegistryRoundTripPostgresFixture fixture)
    : IClassFixture<SchemaRegistryRoundTripPostgresFixture>
{
    private SchemaRegistryRepository FreshRepository() =>
        new(new PostgresRepository(fixture.ConnectionString, NullLogger<PostgresRepository>.Instance));

    // One type that reaches every SchemaDescriptor member SchemaBuilder fills: key and field
    // descriptions, search keys, an embedding (vector field + large field), a chunk (chunk field
    // with contextual set), a summary target, a metadata column, a popularity signal, a text array,
    // two foreign-key columns, a relation, row and field permissions, an owner field, a document
    // template (a synthetic chunk field) and a type description.
    private static SchemaDescriptor OwnerStampedDescriptor()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var td = new TypeDescriptor
        {
            TypeName           = "RoundTripArticle",
            Description        = "An article that exercises every descriptor member",
            DocumentTemplate   = "{Title}: {Body}",
            DocumentMaxTokens  = 384,
            DocumentOverlap    = 48,
            DocumentContextual = true,
            Authorization = new Client.Contracts.AuthorizationRules
            {
                OwnerField     = "OwnerId",
                RowPermissions = { new Client.Contracts.RowPermission { Role = "editors", CanReadAll = true, CanWriteAll = true } },
                FieldPermissions = { new Client.Contracts.FieldPermission { FieldName = "Summary", ReadableRoles = { "editors" } } }
            }
        };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true, Description = "The key" });
        td.Properties.Add(new PropertyDescriptor { Name = "Title", ObjectType = ObjectType.String, IsSearchKey = true, SearchKeyOrder = 1, Description = "Headline" });
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ObjectType = ObjectType.String, IsChunk = true, ChunkMaxTokens = 256, ChunkOverlap = 32, ChunkContextual = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Abstract", ObjectType = ObjectType.String, IsEmbedding = true, IsNullable = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Summary", ObjectType = ObjectType.String, IsSummaryTarget = true, IsNullable = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Category", ObjectType = ObjectType.String, IsMetadata = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Tags", ObjectType = ObjectType.String, IsArray = true });
        td.Properties.Add(new PropertyDescriptor { Name = "ViewedAt", ObjectType = ObjectType.Datetime, IsPopularitySignal = true, IsNullable = true });
        td.Properties.Add(new PropertyDescriptor { Name = "OwnerId", ObjectType = ObjectType.String });
        td.Properties.Add(new PropertyDescriptor { Name = "RoundTripAuthorId", ObjectType = ObjectType.Guid });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Author",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "RoundTripAuthor",
            ForeignKey   = "RoundTripAuthorId"
        });

        return SchemaBuilder.BuildDescriptor(td, embedding) with { OwnerTenantId = "tenant-roundtrip" };
    }

    [Fact]
    public async Task OwnerStampedDescriptor_RoundTripsThroughPostgres_Whole()
    {
        var original = OwnerStampedDescriptor();

        // The worker creates the table at startup; this test owns its own container.
        await FreshRepository().EnsureTableAsync();
        await new SchemaRegistry(FreshRepository(), NullLogger<SchemaRegistry>.Instance).RegisterAsync(original);

        // A second registry over a second repository and connection: what it holds came from the
        // JSON in Postgres, not from the first registry's memory.
        var reader = new SchemaRegistry(FreshRepository(), NullLogger<SchemaRegistry>.Instance);
        await reader.LoadAsync();
        var loaded = reader.Get("RoundTripArticle");

        loaded.Should().NotBeNull();
        loaded!.OwnerTenantId.Should().Be("tenant-roundtrip");
        loaded.Should().BeEquivalentTo(original);
        SchemaBuilder.ToTableSchema(loaded).Should().BeEquivalentTo(
            SchemaBuilder.ToTableSchema(original), options => options.WithStrictOrdering());
    }
}
