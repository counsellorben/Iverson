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
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",          ClrType = ClrType.ClrGuid,     IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Category",    ClrType = ClrType.ClrString,   IsSearchKey = true,  SearchKeyOrder = 0 });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "PublishedAt", ClrType = ClrType.ClrDatetime, IsSearchKey = true,  SearchKeyOrder = 1 });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Body",        ClrType = ClrType.ClrString,   IsLargeField = true });

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
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",          ClrType = ClrType.ClrGuid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Body",        ClrType = ClrType.ClrString, IsLargeField = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "EmbedField",  ClrType = ClrType.ClrString, IsEmbedding  = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "ChunkField",  ClrType = ClrType.ClrString, IsChunk      = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Normal",      ClrType = ClrType.ClrString });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.LargeFieldColumns.Should().BeEquivalentTo(
            new[] { "Body", "EmbedField", "ChunkField" },
            opts => opts.WithoutStrictOrdering());
        descriptor.LargeFieldColumns.Should().NotContain("Normal");
    }

    [Fact]
    public void ToEngagementTableSchema_PopulatesSortKey_AndIncludesAllScalarColumns()
    {
        var descriptor = new SchemaDescriptor
        {
            TypeName          = "Article",
            TableName         = "articles",
            CollectionName    = null,
            KeyColumn         = new ColumnDescriptor("Id",          "UUID",  false),
            ScalarColumns     = [
                new ColumnDescriptor("Category",    "TEXT",        false),
                new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", false),
                new ColumnDescriptor("Body",        "TEXT",        false),
            ],
            FkColumns         = [],
            VectorFields      = [],
            ChunkFields       = [],
            Relations         = [],
            SearchKeyColumns  = ["Category", "PublishedAt"],
            LargeFieldColumns = ["Body"]
        };

        var schema = SchemaBuilder.ToEngagementTableSchema(descriptor);

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
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",       ClrType = ClrType.ClrGuid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Category", ClrType = ClrType.ClrString, IsSearchKey = true, SearchKeyOrder = 0, IsLargeField = true });

        var act = () => SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Category*");
    }

    [Fact]
    public void BuildDescriptor_PopulatesMetadataColumnsAndDescriptions()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Article", Description = "An article." };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",       ClrType = ClrType.ClrGuid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Category", ClrType = ClrType.ClrString, IsMetadata = true, Description = "The category." });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Normal",   ClrType = ClrType.ClrString });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.MetadataColumns.Should().BeEquivalentTo(new[] { "Category" });
        descriptor.MetadataColumns.Should().NotContain("Normal");
        descriptor.FieldDescriptions.Should().ContainKey("Category").WhoseValue.Should().Be("The category.");
        descriptor.FieldDescriptions.Should().NotContainKey("Normal");
        descriptor.Description.Should().Be("An article.");
    }

    [Fact]
    public void BuildDescriptor_CapturesDescription_DeclaredOnTheKeyProperty()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Article" };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",    ClrType = ClrType.ClrGuid,   IsKey = true, Description = "The article id." });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Title", ClrType = ClrType.ClrString, Description = "The title." });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.FieldDescriptions.Should().ContainKey("Id").WhoseValue.Should().Be("The article id.");
        descriptor.FieldDescriptions.Should().ContainKey("Title").WhoseValue.Should().Be("The title.");
    }

    [Fact]
    public void BuildDescriptor_LeavesDescriptionNull_WhenTypeDescriptionIsEmpty()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Article" };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.Description.Should().BeNull();
        descriptor.MetadataColumns.Should().BeEmpty();
        descriptor.FieldDescriptions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("embedding")]
    [InlineData("chunk")]
    [InlineData("array")]
    [InlineData("large")]
    public void BuildDescriptor_Throws_WhenMetadataPropertyIsNotAPlainScalar(string kind)
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var bad = new PropertyDescriptor { Name = "Bad", ClrType = ClrType.ClrString, IsMetadata = true };
        switch (kind)
        {
            case "embedding": bad.IsEmbedding = true; break;
            case "chunk":
                bad.IsChunk         = true;
                bad.ChunkMaxTokens  = 512;
                bad.ChunkOverlap    = 64;
                break;
            case "array":     bad.IsArray = true; break;
            case "large":     bad.IsLargeField = true; break;
        }

        var typeDesc = new TypeDescriptor { TypeName = "Bad" };
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        typeDesc.Properties.Add(bad);

        var act = () => SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Bad*");
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Field")]
    public void BuildDescriptor_Throws_WhenMetadataPropertyCollidesWithReservedChunkPayloadKey(string name)
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Doc" };
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = name, ClrType = ClrType.ClrString, IsMetadata = true });

        var act = () => SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        // Rejected at registration rather than skipped at ingest: a skip would leave the column
        // un-denormalized while BuildChunksFilter still accepted filters on it, so the filter
        // would match the reserved key's value instead.
        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*{name}*reserved chunk payload key*");
    }

    [Fact]
    public void BuildDescriptor_AllowsMetadataPropertyWhoseNameOnlyResemblesAReservedKey()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        // ToCamelCase("ParentId") is "parentId", not the reserved "parent_id" — so this is legal.
        var typeDesc = new TypeDescriptor { TypeName = "Doc" };
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "ParentId", ClrType = ClrType.ClrString, IsMetadata = true });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.MetadataColumns.Should().Contain("ParentId");
    }

    [Fact]
    public void ToEngagementQuerySchema_MapsTypeNameTableNameKeyAndScalarColumns()
    {
        var schema = SchemaFixtures.ArticleSchema();

        var result = SchemaBuilder.ToEngagementQuerySchema(schema);

        result.TypeName.Should().Be("Article");
        result.TableName.Should().Be("articles");
        result.KeyColumnName.Should().Be("Id");
        // AuthorId is both an FK and a scalar column (as SchemaBuilder really produces), so it
        // appears here alongside the plain scalars.
        result.ColumnNames.Should().BeEquivalentTo(["Title", "Body", "AuthorId"]);
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
    public void ToCollectionSchema_IncludesVectorAndCentroidNamedVectors()
    {
        var descriptor = SchemaFixtures.ArticleSchema();

        var schema = SchemaBuilder.ToCollectionSchema(descriptor);

        // ArticleSchema has Title (vector field) and Body (chunk field)
        schema.Vectors.Should().HaveCount(2);
        schema.Vectors.Should().ContainSingle(v => v.Name == "title_vector" && v.Dimension == 768);
        schema.Vectors.Should().ContainSingle(v => v.Name == "body_centroid" && v.Dimension == 768);
    }

    [Fact]
    public void ToChunkCollectionSchema_IncludesPayloadIndex_ForOwnerField_WhenConfigured()
    {
        var descriptor = SchemaFixtures.ArticleSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                "Title",
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>())
        };

        var schema = SchemaBuilder.ToChunkCollectionSchema(descriptor);

        schema.PayloadIndexes.Should().ContainSingle(p => p.FieldName == "title" && p.Kind == PayloadIndexKind.Keyword);
    }

    [Fact]
    public void ToChunkCollectionSchema_OmitsOwnerFieldIndex_WhenNotConfigured()
    {
        var descriptor = SchemaFixtures.ArticleSchema(); // BypassAuthorization() has OwnerField == null

        var schema = SchemaBuilder.ToChunkCollectionSchema(descriptor);

        schema.PayloadIndexes.Should().ContainSingle(p => p.FieldName == "parent_id");
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
    [InlineData(ClrType.ClrString,   true,  "TEXT[]",           "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ClrType.ClrInt32,    false, "INTEGER",          "INT",         PayloadIndexKind.Integer)]
    [InlineData(ClrType.ClrInt32,    true,  "INTEGER[]",        "STRING",      PayloadIndexKind.Integer)]
    [InlineData(ClrType.ClrInt64,    false, "BIGINT",           "BIGINT",      PayloadIndexKind.Integer)]
    [InlineData(ClrType.ClrInt64,    true,  "BIGINT[]",         "STRING",      PayloadIndexKind.Integer)]
    [InlineData(ClrType.ClrFloat,    false, "REAL",             "FLOAT",       PayloadIndexKind.Float)]
    [InlineData(ClrType.ClrFloat,    true,  "REAL[]",           "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ClrType.ClrDouble,   false, "DOUBLE PRECISION", "DOUBLE",      PayloadIndexKind.Float)]
    [InlineData(ClrType.ClrDouble,   true,  "DOUBLE PRECISION[]", "STRING",    PayloadIndexKind.Float)]
    [InlineData(ClrType.ClrBool,     false, "BOOLEAN",          "BOOLEAN",     PayloadIndexKind.Boolean)]
    [InlineData(ClrType.ClrBool,     true,  "BOOLEAN[]",        "STRING",      PayloadIndexKind.Boolean)]
    [InlineData(ClrType.ClrDatetime, false, "TIMESTAMPTZ",      "DATETIME",    PayloadIndexKind.Datetime)]
    [InlineData(ClrType.ClrDatetime, true,  "TIMESTAMPTZ[]",    "STRING",      PayloadIndexKind.Datetime)]
    [InlineData(ClrType.ClrBytes,    false, "BYTEA",            "VARBINARY",   PayloadIndexKind.Keyword)]
    [InlineData(ClrType.ClrBytes,    true,  "BYTEA[]",          "STRING",      PayloadIndexKind.Keyword)]
    public void TypeMapping_IsConsistentAcrossAllThreeConversions(
        ClrType clrType, bool isArray, string expectedSql, string expectedStarRocksType, PayloadIndexKind expectedPayloadKind)
    {
        var sql = SchemaBuilder.ClrTypeToSql(clrType, isArray);

        sql.Should().Be(expectedSql);
        SchemaBuilder.ClrTypeToEngagementType(sql).Should().Be(expectedStarRocksType);
        SchemaBuilder.SqlTypeToPayloadKind(sql).Should().Be(expectedPayloadKind);
    }

    [Fact]
    public void ClrTypeToStarRocksType_UnknownSqlType_FallsBackToString()
    {
        SchemaBuilder.ClrTypeToEngagementType("NOT_A_REAL_TYPE").Should().Be("STRING");
    }

    [Fact]
    public void SqlTypeToPayloadKind_UnknownSqlType_FallsBackToKeyword()
    {
        SchemaBuilder.SqlTypeToPayloadKind("NOT_A_REAL_TYPE").Should().Be(PayloadIndexKind.Keyword);
    }

    // ClrFloat is a deliberate, named exception: it keeps Keyword in the array table because
    // changing it would retype a live Qdrant index. Every other ClrType is element-typed.
    // This table is written out explicitly rather than derived from ScalarTypeMap so it does
    // not silently agree with a future regression on that exact row.
    private static readonly IReadOnlyDictionary<ClrType, PayloadIndexKind> ExpectedArrayPayloadKinds =
        new Dictionary<ClrType, PayloadIndexKind>
        {
            [ClrType.ClrGuid]     = PayloadIndexKind.Keyword,
            [ClrType.ClrString]   = PayloadIndexKind.Keyword,
            [ClrType.ClrInt32]    = PayloadIndexKind.Integer,
            [ClrType.ClrInt64]    = PayloadIndexKind.Integer,
            [ClrType.ClrFloat]    = PayloadIndexKind.Keyword, // named exception — see comment above
            [ClrType.ClrDouble]   = PayloadIndexKind.Float,
            [ClrType.ClrBool]     = PayloadIndexKind.Boolean,
            [ClrType.ClrDatetime] = PayloadIndexKind.Datetime,
            [ClrType.ClrBytes]    = PayloadIndexKind.Keyword
        };

    [Fact]
    public void ArrayTypeOverrides_IsTotalOverClrType()
    {
        foreach (var clrType in Enum.GetValues<ClrType>())
        {
            var scalarSql = SchemaBuilder.ClrTypeToSql(clrType, isArray: false);
            var arraySql = SchemaBuilder.ClrTypeToSql(clrType, isArray: true);

            arraySql.Should().Be(scalarSql + "[]", $"array SQL type for {clrType} should be its scalar type plus []");
            SchemaBuilder.ClrTypeToEngagementType(arraySql).Should().Be("STRING", $"StarRocks type for array {clrType} should be STRING");
            SchemaBuilder.SqlTypeToPayloadKind(arraySql).Should().Be(
                ExpectedArrayPayloadKinds[clrType],
                $"payload kind for array {clrType} should match the expected table");
        }
    }

    [Fact]
    public void SqlTypeToClr_RecoversEveryClrType_ScalarAndArray()
    {
        foreach (var clrType in Enum.GetValues<ClrType>())
        {
            var scalarSql = SchemaBuilder.ClrTypeToSql(clrType, isArray: false);
            SchemaBuilder.SqlTypeToClr(scalarSql).Should().Be((clrType, false),
                $"scalar SQL type for {clrType} should map back to ({clrType}, false)");

            var arraySql = SchemaBuilder.ClrTypeToSql(clrType, isArray: true);
            SchemaBuilder.SqlTypeToClr(arraySql).Should().Be((clrType, true),
                $"array SQL type for {clrType} should map back to ({clrType}, true)");
        }
    }
}
