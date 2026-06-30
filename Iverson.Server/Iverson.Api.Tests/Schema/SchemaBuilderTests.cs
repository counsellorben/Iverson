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

        descriptor.SearchKeyColumns.Should().Equal("Category", "PublishedAt");
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
    public void ToStarRocksTableSchema_PopulatesSortKey_AndIncludesAllScalarColumns()
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
            SearchKeyColumns  = ["Category", "PublishedAt"],
            LargeFieldColumns = ["Body"]
        };

        var schema = SchemaBuilder.ToStarRocksTableSchema(descriptor);

        schema.SortKey.Should().Equal("Category", "PublishedAt");
        schema.Columns.Select(c => c.Name).Should().Contain("Body");
        schema.Columns.Select(c => c.Name).Should().Contain("Category");
    }

    [Fact]
    public void BuildDescriptor_Throws_WhenPropertyHasBothSearchKeyAndLargeField()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Bad" };
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id",       ClrType = ClrType.ClrGuid,   IsKey = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Category", ClrType = ClrType.ClrString, IsSearchKey = true, SearchKeyOrder = 0, IsLargeField = true });

        var act = () => SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Category*");
    }
}
