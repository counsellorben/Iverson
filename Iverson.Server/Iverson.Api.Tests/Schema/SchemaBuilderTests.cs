using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Vector;
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

    [Fact]
    public void ToStarRocksQuerySchema_MapsTypeNameTableNameKeyAndScalarColumns()
    {
        var schema = SchemaFixtures.ArticleSchema();

        var result = SchemaBuilder.ToStarRocksQuerySchema(schema);

        result.TypeName.Should().Be("Article");
        result.TableName.Should().Be("articles");
        result.KeyColumnName.Should().Be("Id");
        result.ColumnNames.Should().BeEquivalentTo(["Title", "Body"]);
    }

    [Fact]
    public void ToCollectionSchema_PayloadIndexNames_AreCamelCase()
    {
        var descriptor = SchemaFixtures.ArticleSchema();

        var schema = SchemaBuilder.ToCollectionSchema(descriptor);

        schema.PayloadIndexes.Select(p => p.FieldName).Should().Contain(["title", "body", "authorId"]);
        schema.PayloadIndexes.Select(p => p.FieldName).Should().NotContain(["Title", "Body", "AuthorId"]);
    }

    [Fact]
    public void BuildDescriptor_ManyToManyRelation_MapsToInternalManyToMany()
    {
        var td = new TypeDescriptor { TypeName = "Article" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        td.Relations.Add(new Iverson.Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tags",
            Kind         = Iverson.Client.Contracts.RelationKind.ManyToMany,
            RelatedType  = "Tag",
            ForeignKey   = "TagIds"
        });
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var descriptor = SchemaBuilder.BuildDescriptor(td, embedding);

        descriptor.Relations.Single().Kind.Should().Be(Iverson.Api.Schema.RelationKind.ManyToMany);
    }

    [Theory]
    [InlineData(ClrType.ClrGuid,     false, "UUID",             "VARCHAR(36)", PayloadIndexKind.Keyword)]
    [InlineData(ClrType.ClrGuid,     true,  "UUID[]",           "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ClrType.ClrString,   false, "TEXT",             "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ClrType.ClrInt32,    false, "INTEGER",          "INT",         PayloadIndexKind.Integer)]
    [InlineData(ClrType.ClrInt64,    false, "BIGINT",           "BIGINT",      PayloadIndexKind.Integer)]
    [InlineData(ClrType.ClrFloat,    false, "REAL",             "FLOAT",       PayloadIndexKind.Float)]
    [InlineData(ClrType.ClrFloat,    true,  "REAL[]",           "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ClrType.ClrDouble,   false, "DOUBLE PRECISION", "DOUBLE",      PayloadIndexKind.Float)]
    [InlineData(ClrType.ClrBool,     false, "BOOLEAN",          "BOOLEAN",     PayloadIndexKind.Boolean)]
    [InlineData(ClrType.ClrDatetime, false, "TIMESTAMPTZ",      "DATETIME",    PayloadIndexKind.Datetime)]
    [InlineData(ClrType.ClrBytes,    false, "BYTEA",            "VARBINARY",   PayloadIndexKind.Keyword)]
    public void TypeMapping_IsConsistentAcrossAllThreeConversions(
        ClrType clrType, bool isArray, string expectedSql, string expectedStarRocksType, PayloadIndexKind expectedPayloadKind)
    {
        var sql = SchemaBuilder.ClrTypeToSql(clrType, isArray);

        sql.Should().Be(expectedSql);
        SchemaBuilder.ClrTypeToStarRocksType(sql).Should().Be(expectedStarRocksType);
        SchemaBuilder.SqlTypeToPayloadKind(sql).Should().Be(expectedPayloadKind);
    }

    [Fact]
    public void ClrTypeToStarRocksType_UnknownSqlType_FallsBackToString()
    {
        SchemaBuilder.ClrTypeToStarRocksType("NOT_A_REAL_TYPE").Should().Be("STRING");
    }

    [Fact]
    public void SqlTypeToPayloadKind_UnknownSqlType_FallsBackToKeyword()
    {
        SchemaBuilder.SqlTypeToPayloadKind("NOT_A_REAL_TYPE").Should().Be(PayloadIndexKind.Keyword);
    }
}
