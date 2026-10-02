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
            Properties = { new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true } },
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
            new PropertyDescriptor { Name = "Id",          ObjectType = ObjectType.Guid,     IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Category",    ObjectType = ObjectType.String,   IsSearchKey = true,  SearchKeyOrder = 0 });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "PublishedAt", ObjectType = ObjectType.Datetime, IsSearchKey = true,  SearchKeyOrder = 1 });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Body",        ObjectType = ObjectType.String,   IsLargeField = true });

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
            new PropertyDescriptor { Name = "Id",          ObjectType = ObjectType.Guid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Body",        ObjectType = ObjectType.String, IsLargeField = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "EmbedField",  ObjectType = ObjectType.String, IsEmbedding  = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "ChunkField",  ObjectType = ObjectType.String, IsChunk      = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Normal",      ObjectType = ObjectType.String });

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
            LargeFieldColumns = ["Body"],
            // Required since Task 7 made SchemaDescriptor.TenantColumn non-nullable.
            TenantColumn      = SchemaDescriptor.TenantColumnName
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
            new PropertyDescriptor { Name = "Id",       ObjectType = ObjectType.Guid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Category", ObjectType = ObjectType.String, IsSearchKey = true, SearchKeyOrder = 0, IsLargeField = true });

        var act = () => SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Category*");
    }

    [Fact]
    public void BuildDescriptor_Throws_WhenTwoPropertiesAreChunked()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Bad" };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",      ObjectType = ObjectType.Guid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Body",    ObjectType = ObjectType.String, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Summary", ObjectType = ObjectType.String, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });

        var act = () => SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Body*Summary*");
    }

    // The rule counts attributed properties, not ChunkFields: a document template synthesizes a
    // second "Document" chunk field, and that pairing is legal. This test is what discriminates
    // the two implementations -- a `chunks.Count > 1` guard passes the test above and fails here.
    [Fact]
    public void BuildDescriptor_AllowsOneChunkedProperty_AlongsideADocumentTemplate()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Article", DocumentTemplate = "{Title}" };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",    ObjectType = ObjectType.Guid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Title", ObjectType = ObjectType.String });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Body",  ObjectType = ObjectType.String, IsChunk = true, ChunkMaxTokens = 512, ChunkOverlap = 64 });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.ChunkFields.Select(c => c.PropertyName).Should().BeEquivalentTo(["Body", "Document"]);
    }

    [Fact]
    public void BuildDescriptor_PopulatesMetadataColumnsAndDescriptions()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Article", Description = "An article." };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",       ObjectType = ObjectType.Guid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Category", ObjectType = ObjectType.String, IsMetadata = true, Description = "The category." });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Normal",   ObjectType = ObjectType.String });

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
            new PropertyDescriptor { Name = "Id",    ObjectType = ObjectType.Guid,   IsKey = true, Description = "The article id." });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Title", ObjectType = ObjectType.String, Description = "The title." });

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
            new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });

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

        var bad = new PropertyDescriptor { Name = "Bad", ObjectType = ObjectType.String, IsMetadata = true };
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
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
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
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = name, ObjectType = ObjectType.String, IsMetadata = true });

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
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
        typeDesc.Properties.Add(new PropertyDescriptor { Name = "ParentId", ObjectType = ObjectType.String, IsMetadata = true });

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
    public void ToEngagementQuerySchema_CarriesTheTenantColumnNameThrough()
    {
        // Iverson.StarRocks cannot see SchemaDescriptor.TenantColumnName (no project reference),
        // so the name travels as DATA on EngagementQuerySchema. Every tenant-column exclusion in
        // StarRocksQueryBuilder/StarRocksPipelineBuilder is gated on this field being populated:
        // if this adapter stopped passing it, all of them would silently go inert in production
        // while their own unit tests — which construct EngagementQuerySchema directly — stayed
        // green. This is the test that catches that.
        var schema = SchemaFixtures.ArticleSchema() with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false)
            ],
            TenantColumn = SchemaDescriptor.TenantColumnName
        };

        var result = SchemaBuilder.ToEngagementQuerySchema(schema);

        result.TenantColumnName.Should().Be(SchemaDescriptor.TenantColumnName);
        result.IsTenantColumn(SchemaDescriptor.TenantColumnName).Should().BeTrue();
        // Still a real physical column in the list — the read-time tenant predicate needs it.
        result.ColumnNames.Should().Contain(SchemaDescriptor.TenantColumnName);
    }

    [Fact]
    public void ToEngagementQuerySchema_LegacyClientDeclaredTenantColumn_IsNotCarriedAsAnExclusionKey()
    {
        // RULING 70. SchemaRegistry.LoadAsync admits a pre-cutover _iverson_schema row verbatim, and
        // such a row persisted TenantColumn as the CLIENT-DECLARED tenant_field name — typically
        // "TenantId". EngagementQuerySchema.TenantColumnName is not "the column that carries this
        // schema's boundary"; it is the name StarRocksQueryBuilder/StarRocksPipelineBuilder refuse
        // to project, to resolve for a caller, to sort on, and to track in a pipeline step. Every
        // one of those exclusions in Iverson.Api is keyed on the RESERVED spelling and deliberately
        // leaves a client-declared column alone (AuthorizationFieldMasking.RemoveTenantColumn says
        // so in as many words), so this adapter — the only place that can see the constant — is
        // where the two are made to agree.
        //
        // Carry the raw value instead and an upgraded deployment with one un-re-registered type
        // gets SILENTLY WRONG RESULTS, not an error: Search(filter: TenantId == "team-a") loses the
        // clause entirely, because an unresolvable filter property is dropped rather than rejected.
        var schema = SchemaFixtures.ArticleSchema() with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("TenantId", "TEXT", false)
            ],
            TenantColumn = "TenantId"
        };

        var result = SchemaBuilder.ToEngagementQuerySchema(schema);

        result.TenantColumnName.Should().BeNull(
            "a client-declared tenant column is not the reserved server-owned one, and it is the "
            + "reserved name that every exclusion on both sides of the boundary is keyed on");
        result.IsTenantColumn("TenantId").Should().BeFalse(
            "this is the predicate StarRocksQueryBuilder.BuildSelectColumns and ResolveColumn "
            + "consult — true here removes the client's own declared column from the projection "
            + "and makes a filter or sort on it resolve to null, which is silently dropped");
        result.ColumnNames.Should().Contain("TenantId",
            "the column is still physically there and the legacy schema is still scoped by it — "
            + "the tenant predicate is spliced from AuthorizationConstraint.TenantColumn, which "
            + "never goes through ResolveColumn");
    }

    [Fact]
    public void ToTableSchema_LegacyClientDeclaredTenantColumn_IsCarriedRaw()
    {
        // The COUNTERPART to the test above, and the reason the gating belongs on
        // ToEngagementQuerySchema alone. TableSchema.TenantColumn is the BOUNDARY column itself:
        // PostgresSchemaManager predicates the RLS policy on it and the write path injects the
        // tenant value into it. Gating this one on the reserved name would leave a legacy table
        // with no RLS policy and no tenant injection — a boundary break, the opposite of the
        // silent-wrong-results the other gating prevents.
        var schema = SchemaFixtures.ArticleSchema() with { TenantColumn = "TenantId" };

        SchemaBuilder.ToTableSchema(schema).TenantColumn.Should().Be("TenantId");
    }

    [Fact]
    public void ToTableSchema_CarriesTheTenantColumnNameThrough()
    {
        // The OutboxWriter injection is gated on TableSchema.TenantColumn for the same reason.
        var schema = SchemaFixtures.ArticleSchema() with
        {
            TenantColumn = SchemaDescriptor.TenantColumnName
        };

        SchemaBuilder.ToTableSchema(schema).TenantColumn.Should().Be(SchemaDescriptor.TenantColumnName);
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
    public void ToChunkCollectionSchema_IncludesPayloadIndex_ForField()
    {
        // final-review Finding 4: IntelligenceStoreConsumer's orphan-delete pass filters
        // DeleteByFilterAsync on parent_id AND field on every chunk write. Without a payload
        // index for "field", Qdrant intersects an indexed parent_id match against an unindexed
        // field scan on the hot path for every pre-existing chunked type.
        var descriptor = SchemaFixtures.ArticleSchema();

        var schema = SchemaBuilder.ToChunkCollectionSchema(descriptor);

        schema.PayloadIndexes.Should().ContainSingle(p => p.FieldName == "field" && p.Kind == PayloadIndexKind.Keyword);
    }

    [Fact]
    public void BuildDescriptor_ManyToManyRelation_MapsToInternalManyToMany()
    {
        var td = new TypeDescriptor { TypeName = "Article" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
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
    [InlineData(ObjectType.Guid,     false, "UUID",             "VARCHAR(36)", PayloadIndexKind.Keyword)]
    [InlineData(ObjectType.Guid,     true,  "UUID[]",           "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ObjectType.String,   false, "TEXT",             "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ObjectType.String,   true,  "TEXT[]",           "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ObjectType.Int32,    false, "INTEGER",          "INT",         PayloadIndexKind.Integer)]
    [InlineData(ObjectType.Int32,    true,  "INTEGER[]",        "STRING",      PayloadIndexKind.Integer)]
    [InlineData(ObjectType.Int64,    false, "BIGINT",           "BIGINT",      PayloadIndexKind.Integer)]
    [InlineData(ObjectType.Int64,    true,  "BIGINT[]",         "STRING",      PayloadIndexKind.Integer)]
    [InlineData(ObjectType.Float,    false, "REAL",             "FLOAT",       PayloadIndexKind.Float)]
    [InlineData(ObjectType.Float,    true,  "REAL[]",           "STRING",      PayloadIndexKind.Keyword)]
    [InlineData(ObjectType.Double,   false, "DOUBLE PRECISION", "DOUBLE",      PayloadIndexKind.Float)]
    [InlineData(ObjectType.Double,   true,  "DOUBLE PRECISION[]", "STRING",    PayloadIndexKind.Float)]
    [InlineData(ObjectType.Bool,     false, "BOOLEAN",          "BOOLEAN",     PayloadIndexKind.Boolean)]
    [InlineData(ObjectType.Bool,     true,  "BOOLEAN[]",        "STRING",      PayloadIndexKind.Boolean)]
    [InlineData(ObjectType.Datetime, false, "TIMESTAMPTZ",      "DATETIME",    PayloadIndexKind.Datetime)]
    [InlineData(ObjectType.Datetime, true,  "TIMESTAMPTZ[]",    "STRING",      PayloadIndexKind.Datetime)]
    [InlineData(ObjectType.Bytes,    false, "BYTEA",            "VARBINARY",   PayloadIndexKind.Keyword)]
    [InlineData(ObjectType.Bytes,    true,  "BYTEA[]",          "STRING",      PayloadIndexKind.Keyword)]
    public void TypeMapping_IsConsistentAcrossAllThreeConversions(
        ObjectType objectType, bool isArray, string expectedSql, string expectedStarRocksType, PayloadIndexKind expectedPayloadKind)
    {
        var sql = SchemaBuilder.ObjectTypeToSql(objectType, isArray);

        sql.Should().Be(expectedSql);
        SchemaBuilder.ObjectTypeToEngagementType(sql).Should().Be(expectedStarRocksType);
        SchemaBuilder.SqlTypeToPayloadKind(sql).Should().Be(expectedPayloadKind);
    }

    [Fact]
    public void ObjectTypeToStarRocksType_UnknownSqlType_FallsBackToString()
    {
        SchemaBuilder.ObjectTypeToEngagementType("NOT_A_REAL_TYPE").Should().Be("STRING");
    }

    [Fact]
    public void SqlTypeToPayloadKind_UnknownSqlType_FallsBackToKeyword()
    {
        SchemaBuilder.SqlTypeToPayloadKind("NOT_A_REAL_TYPE").Should().Be(PayloadIndexKind.Keyword);
    }

    // Float is a deliberate, named exception: it keeps Keyword in the array table because
    // changing it would retype a live Qdrant index. Every other ObjectType is element-typed.
    // This table is written out explicitly rather than derived from ScalarTypeMap so it does
    // not silently agree with a future regression on that exact row.
    private static readonly IReadOnlyDictionary<ObjectType, PayloadIndexKind> ExpectedArrayPayloadKinds =
        new Dictionary<ObjectType, PayloadIndexKind>
        {
            [ObjectType.Guid]     = PayloadIndexKind.Keyword,
            [ObjectType.String]   = PayloadIndexKind.Keyword,
            [ObjectType.Int32]    = PayloadIndexKind.Integer,
            [ObjectType.Int64]    = PayloadIndexKind.Integer,
            [ObjectType.Float]    = PayloadIndexKind.Keyword, // named exception — see comment above
            [ObjectType.Double]   = PayloadIndexKind.Float,
            [ObjectType.Bool]     = PayloadIndexKind.Boolean,
            [ObjectType.Datetime] = PayloadIndexKind.Datetime,
            [ObjectType.Bytes]    = PayloadIndexKind.Keyword
        };

    [Fact]
    public void ArrayTypeOverrides_IsTotalOverObjectType()
    {
        foreach (var objectType in Enum.GetValues<ObjectType>())
        {
            var scalarSql = SchemaBuilder.ObjectTypeToSql(objectType, isArray: false);
            var arraySql = SchemaBuilder.ObjectTypeToSql(objectType, isArray: true);

            arraySql.Should().Be(scalarSql + "[]", $"array SQL type for {objectType} should be its scalar type plus []");
            SchemaBuilder.ObjectTypeToEngagementType(arraySql).Should().Be("STRING", $"StarRocks type for array {objectType} should be STRING");
            SchemaBuilder.SqlTypeToPayloadKind(arraySql).Should().Be(
                ExpectedArrayPayloadKinds[objectType],
                $"payload kind for array {objectType} should match the expected table");
        }
    }

    [Fact]
    public void SqlTypeToObjectType_RecoversEveryObjectType_ScalarAndArray()
    {
        foreach (var objectType in Enum.GetValues<ObjectType>())
        {
            var scalarSql = SchemaBuilder.ObjectTypeToSql(objectType, isArray: false);
            SchemaBuilder.SqlTypeToObjectType(scalarSql).Should().Be((objectType, false),
                $"scalar SQL type for {objectType} should map back to ({objectType}, false)");

            var arraySql = SchemaBuilder.ObjectTypeToSql(objectType, isArray: true);
            SchemaBuilder.SqlTypeToObjectType(arraySql).Should().Be((objectType, true),
                $"array SQL type for {objectType} should map back to ({objectType}, true)");
        }
    }

    [Fact]
    public void BuildDescriptor_WithDocumentTemplate_AppendsDocumentChunkField()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor
        {
            TypeName         = "Article",
            Properties       = { new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true } },
            DocumentTemplate = "{Title}"
        };

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.ChunkFields.Should().ContainSingle(c => c.PropertyName == "Document");
        var chunk = descriptor.ChunkFields.Single(c => c.PropertyName == "Document");
        chunk.ModelId.Should().Be("nomic-embed-text");
        chunk.Dimension.Should().Be(768);
        descriptor.CollectionName.Should().NotBeNull();
        descriptor.LargeFieldColumns.Should().NotContain("Document");
    }

    [Fact]
    public void BuildDescriptor_WithDocumentTemplate_UnsetTokenFields_DefaultToFallbackValues()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor
        {
            TypeName         = "Article",
            Properties       = { new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true } },
            DocumentTemplate = "{Title}"
            // DocumentMaxTokens / DocumentOverlap left unset (proto3 default 0).
        };

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        var chunk = descriptor.ChunkFields.Single(c => c.PropertyName == "Document");
        chunk.MaxTokens.Should().Be(512);
        chunk.Overlap.Should().Be(64);
    }

    [Fact]
    public void BuildDescriptor_WithoutDocumentTemplate_IsByteIdenticalToPreExistingBehavior()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor
        {
            TypeName   = "Article",
            Properties = { new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true } }
        };

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.ChunkFields.Should().BeEmpty();
        descriptor.VectorFields.Should().BeEmpty();
        descriptor.CollectionName.Should().BeNull();
        descriptor.DocumentTemplate.Should().BeNull();
        descriptor.DocumentTemplateSource.Should().BeNull();
    }

    [Fact]
    public void BuildDescriptor_MapsSinglePopularitySignalProperty_ToPopularitySignalColumn()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Comment" };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",        ObjectType = ObjectType.Guid,     IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "PostedAt",  ObjectType = ObjectType.Datetime, IsPopularitySignal = true });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.PopularitySignalColumn.Should().Be("PostedAt");
    }

    [Fact]
    public void BuildDescriptor_LeavesPopularitySignalColumnNull_WhenNoPropertyIsMarked()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Comment" };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",       ObjectType = ObjectType.Guid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "PostedAt", ObjectType = ObjectType.Datetime });

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.PopularitySignalColumn.Should().BeNull();
    }

    [Fact]
    public void BuildDescriptor_Throws_WhenMultiplePropertiesCarryPopularitySignal()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Comment" };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",        ObjectType = ObjectType.Guid,     IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "PostedAt",  ObjectType = ObjectType.Datetime, IsPopularitySignal = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "EditedAt",  ObjectType = ObjectType.Datetime, IsPopularitySignal = true });

        var act = () => SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PostedAt*EditedAt*");
    }

    [Fact]
    public void BuildDescriptor_Throws_WhenPopularitySignalPropertyIsNotDatetime()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor { TypeName = "Comment" };
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "Id",         ObjectType = ObjectType.Guid,   IsKey = true });
        typeDesc.Properties.Add(
            new PropertyDescriptor { Name = "InteractedAt", ObjectType = ObjectType.String, IsPopularitySignal = true });

        var act = () => SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*InteractedAt*");
    }
}
