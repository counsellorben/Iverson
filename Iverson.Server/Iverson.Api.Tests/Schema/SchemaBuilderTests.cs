using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public class SchemaBuilderTests
{
    [Fact]
    public void BuildDescriptor_InfersTableNameFromTypeName()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor
        {
            TypeName   = "Article",
            Properties = { new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true } },
            Relations  = { }
        };

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.TableName.Should().Be("articles");
        descriptor.KeyColumn.Name.Should().Be("Id");
    }

    [Fact]
    public void BuildDescriptor_PopulatesSearchKeyColumns_FromIsSearchKeyProperties()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Article" };
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id",          ClrType = ClrType.ClrGuid,     IsKey = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Category",    ClrType = ClrType.ClrString,   IsSearchKey = true,  SearchKeyOrder = 0 });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "PublishedAt", ClrType = ClrType.ClrDatetime, IsSearchKey = true,  SearchKeyOrder = 1 });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Body",        ClrType = ClrType.ClrString,   IsLargeField = true });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.SearchKeyColumns.Should().HaveCount(2);
        descriptor.SearchKeyColumns[0].Should().Be(("Category", 0));
        descriptor.SearchKeyColumns[1].Should().Be(("PublishedAt", 1));
    }

    [Fact]
    public void BuildDescriptor_PopulatesLargeFieldColumns_FromExplicitAndImplicitSources()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Article" };
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id",          ClrType = ClrType.ClrGuid,   IsKey = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Body",        ClrType = ClrType.ClrString, IsLargeField = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "EmbedField",  ClrType = ClrType.ClrString, IsEmbedding  = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "ChunkField",  ClrType = ClrType.ClrString, IsChunk      = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Normal",      ClrType = ClrType.ClrString });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.LargeFieldColumns.Should().BeEquivalentTo(
            new[] { "Body", "EmbedField", "ChunkField" },
            opts => opts.WithoutStrictOrdering());
        descriptor.LargeFieldColumns.Should().NotContain("Normal");
    }

    [Fact]
    public void ToStarRocksTableSchema_PopulatesMvSortKey_AndMvExcludedColumns()
    {
        var descriptor = new SchemaDescriptor
        {
            TypeName       = "Article",
            TableName      = "articles",
            CollectionName = null,
            KeyColumn      = new ColumnDescriptor("Id",          "UUID",  false),
            ScalarColumns  = [
                new ColumnDescriptor("Category",    "TEXT",        false),
                new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", false),
                new ColumnDescriptor("Body",        "TEXT",        false),
            ],
            FkColumns    = [],
            VectorFields = [],
            ChunkFields  = [],
            Relations    = [],
            SearchKeyColumns = [("Category", 0), ("PublishedAt", 1)],
            LargeFieldColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Body" }
        };

        var schema = SchemaBuilder.ToStarRocksTableSchema(descriptor);

        schema.MvSortKey.Should().Equal("Category", "PublishedAt");
        schema.MvExcludedColumns.Should().Contain("Body");
        schema.MvExcludedColumns.Should().NotContain("Category");
    }
}
