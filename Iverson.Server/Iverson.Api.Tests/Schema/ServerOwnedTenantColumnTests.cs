using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Security.Claims;
using Xunit;
using SchemaAuthorizationRules = Iverson.Api.Schema.AuthorizationRules;
using SchemaFieldPermission    = Iverson.Api.Schema.FieldPermission;
using SchemaRowPermission      = Iverson.Api.Schema.RowPermission;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;
using SchemaRelationKind       = Iverson.Api.Schema.RelationKind;

namespace Iverson.Api.Tests.Schema;

/// <summary>
/// Task 1 of the remove-IversonTenant plan: the SERVER owns the tenant column outright, under the
/// reserved name <c>__TenantId</c>. These tests pin (a) that SchemaBuilder injects it regardless of
/// what the client declared, (b) its SQL type and nullability, and (c) the in/out position of each
/// <c>ScalarColumns</c> consumer that can be exercised without a gRPC harness. The consumers that
/// need one are covered in ObjectMappingGrpcServiceTests, ObjectSearchGrpcServiceTests,
/// DocumentTemplateValidationTests and IntelligenceStoreConsumerTests.
/// </summary>
public class ServerOwnedTenantColumnTests
{
    private static IEmbeddingService Embedding()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");
        return embedding;
    }

    private static TypeDescriptor ArticleType(string? tenantField = null)
    {
        var td = new TypeDescriptor { TypeName = "Article" };
        if (tenantField is not null) td.TenantField = tenantField;
        td.Properties.Add(new PropertyDescriptor { Name = "Id",    ClrType = ClrType.ClrGuid,   IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Title", ClrType = ClrType.ClrString });
        return td;
    }

    // ── Injection ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildDescriptor_InjectsTheServerOwnedTenantColumn_WhenTheClientDeclaresNoTenantField()
    {
        var descriptor = SchemaBuilder.BuildDescriptor(ArticleType(), Embedding());

        descriptor.ScalarColumns.Select(c => c.Name)
            .Should().Contain(SchemaDescriptor.TenantColumnName);
        descriptor.TenantColumn.Should().Be(SchemaDescriptor.TenantColumnName);
    }

    [Fact]
    public void BuildDescriptor_TenantColumn_IsTextAndNotNull()
    {
        // Both halves are load-bearing, hence one assertion each rather than a shape check:
        //  * TEXT — SchemaRegistrationOrchestrator.ValidateFieldReference runs for the tenant field
        //    on EVERY registration and rejects any SqlType outside TEXT/UUID/BYTEA/TIMESTAMPTZ, and
        //    PostgresSchemaManager's RLS predicate compares the column to a text current_setting.
        //  * NOT NULL — so the silent-overwrite path introduced in Task 2 fails loudly with a
        //    constraint violation instead of orphaning a row behind RLS.
        var descriptor = SchemaBuilder.BuildDescriptor(ArticleType(), Embedding());

        var tenant = descriptor.ScalarColumns
            .Single(c => c.Name == SchemaDescriptor.TenantColumnName);

        tenant.SqlType.Should().Be("TEXT");
        tenant.IsNullable.Should().BeFalse();
    }

    [Fact]
    public void BuildDescriptor_IgnoresAClientDeclaredTenantField_AndStillOwnsTheColumn()
    {
        // The client's tenant_field no longer participates in deriving TenantColumn. (Task 4
        // turns the now-dead registration guard into an outright rejection of a declared
        // tenant_field; Task 1 only stops honouring it.)
        var td = ArticleType(tenantField: "Title");

        var descriptor = SchemaBuilder.BuildDescriptor(td, Embedding());

        descriptor.TenantColumn.Should().Be(SchemaDescriptor.TenantColumnName);
    }

    // ── Include: the four SchemaBuilder projections ───────────────────────────

    [Fact]
    public void ToTableSchema_IncludesTheTenantColumn()
    {
        var descriptor = SchemaBuilder.BuildDescriptor(ArticleType(), Embedding());

        var table = SchemaBuilder.ToTableSchema(descriptor);

        table.Columns.Select(c => c.Name).Should().Contain(SchemaDescriptor.TenantColumnName);
        table.TenantColumn.Should().Be(SchemaDescriptor.TenantColumnName);
    }

    [Fact]
    public void ToEngagementTableSchema_IncludesTheTenantColumn()
    {
        var descriptor = SchemaBuilder.BuildDescriptor(ArticleType(), Embedding());

        var table = SchemaBuilder.ToEngagementTableSchema(descriptor);

        table.Columns.Select(c => c.Name).Should().Contain(SchemaDescriptor.TenantColumnName);
        table.Columns.Single(c => c.Name == SchemaDescriptor.TenantColumnName)
            .IsNullable.Should().BeFalse();
    }

    [Fact]
    public void ToEngagementQuerySchema_IncludesTheTenantColumn()
    {
        var descriptor = SchemaBuilder.BuildDescriptor(ArticleType(), Embedding());

        SchemaBuilder.ToEngagementQuerySchema(descriptor)
            .ColumnNames.Should().Contain(SchemaDescriptor.TenantColumnName);
    }

    [Fact]
    public void ToCollectionSchema_IncludesTheTenantColumnPayloadIndex()
    {
        var td = ArticleType();
        td.Properties.Add(new PropertyDescriptor { Name = "Body", ClrType = ClrType.ClrString, IsEmbedding = true });
        var descriptor = SchemaBuilder.BuildDescriptor(td, Embedding());

        var collection = SchemaBuilder.ToCollectionSchema(descriptor);

        // ToCamelCase leaves a leading underscore alone, so the payload key is the column name.
        collection.PayloadIndexes.Select(i => i.FieldName)
            .Should().Contain(SchemaDescriptor.TenantColumnName);
    }

    // ── Exclude: DecayFieldResolver ───────────────────────────────────────────

    [Fact]
    public void ResolveDecayField_NeverChoosesTheTenantColumn_EvenWhenItLooksLikeACandidate()
    {
        // Hand-built: SchemaBuilder can never produce this shape (the tenant column is TEXT and is
        // not a metadata column), so this pins the exclusion filter itself rather than an
        // accidental type mismatch.
        var schema = new SchemaDescriptor
        {
            TypeName        = $"Decay_{Guid.NewGuid():N}",
            TableName       = "decays",
            KeyColumn       = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns   = [new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TIMESTAMPTZ", false)],
            FkColumns       = [],
            VectorFields    = [],
            ChunkFields     = [],
            Relations       = [],
            MetadataColumns = [SchemaDescriptor.TenantColumnName],
            TenantColumn    = SchemaDescriptor.TenantColumnName
        };

        DecayFieldResolver.ResolveDecayField(schema, NullLogger.Instance).Should().BeNull();
    }

    // ── Exclude: RowFieldAuthorizationEvaluator ───────────────────────────────

    [Fact]
    public void Evaluate_AllowedFields_NeverContainsTheTenantColumn()
    {
        var schema = new SchemaDescriptor
        {
            TypeName      = "Article",
            TableName     = "articles",
            KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns =
            [
                new ColumnDescriptor("Name", "text", false),
                new ColumnDescriptor("Secret", "text", true),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false)
            ],
            FkColumns     = [],
            VectorFields  = [],
            ChunkFields   = [],
            Relations     = [],
            TenantColumn  = SchemaDescriptor.TenantColumnName,
            Authorization = new SchemaAuthorizationRules(
                null,
                [new SchemaRowPermission("admin", true, true, true)],
                [new SchemaFieldPermission("Secret", ["premium"], ["premium"])])
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "u1"), new Claim("tenant_id", "t1"), new Claim("groups", "admin")], "test"));

        var result = new RowFieldAuthorizationEvaluator().Evaluate(schema, user, AuthorizationAction.Read);

        result.Denied.Should().BeFalse();
        result.AllowedFields.Should().NotBeNull();
        result.AllowedFields!.Should().Contain("Name");
        result.AllowedFields!.Should().NotContain(SchemaDescriptor.TenantColumnName);
    }

    // ── Exclude: ObjectSearchGrpcService.TimestampColumns ──────────────────────

    [Fact]
    public void TimestampColumns_NeverContainsTheTenantColumn()
    {
        // Same hand-built shape as the decay test, and for the same reason: the exclusion has to be
        // pinned on its own, not on the tenant column's real TEXT type.
        var schema = new SchemaDescriptor
        {
            TypeName      = "Article",
            TableName     = "articles",
            KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns =
            [
                new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", false),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TIMESTAMPTZ", false)
            ],
            FkColumns     = [],
            VectorFields  = [],
            ChunkFields   = [],
            Relations     = [],
            TenantColumn  = SchemaDescriptor.TenantColumnName
        };

        var columns = ObjectSearchGrpcService.TimestampColumns(schema);

        columns.Should().Contain("publishedAt");
        columns.Should().NotContain(SchemaDescriptor.TenantColumnName);
    }

    // ── Exclude: RelationValidator's FK column lookup ─────────────────────────

    [Fact]
    public void ValidateAndNormalizeRelations_DoesNotResolveARelationForeignKeyToTheTenantColumn()
    {
        // A relation naming __TenantId as its foreign key must NOT resolve to the server-owned
        // column and be treated as the declared FK.
        //
        // The tenant column is declared NULLABLE here on purpose. With IsNullable: false the
        // exclusion is unobservable — `fkCol is null || !fkCol.IsNullable` errors either way — so a
        // test built that way is green whether or not the exclusion exists (verified by mutation:
        // it survived removing the filter). Nullable is the shape that discriminates: WITHOUT the
        // exclusion the lookup finds a nullable column and the required relation is silently
        // accepted; WITH it the column is invisible and the relation is rejected.
        //
        // And this shape is reachable, not hypothetical: SchemaRegistry.LoadAsync rehydrates
        // descriptors straight from _iverson_schema JSON without re-running BuildDescriptor or the
        // orchestrator (the same reason RelationValidator already handles rehydrated collisions),
        // so a descriptor whose __TenantId is nullable CAN reach this validator in production.
        var schema = new SchemaDescriptor
        {
            TypeName      = "Article",
            TableName     = "articles",
            KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns = [new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", true)],
            FkColumns     = [],
            VectorFields  = [],
            ChunkFields   = [],
            Relations     =
            [
                new SchemaRelationDescriptor("Tenant", SchemaRelationKind.ManyToOne, "Tenant", SchemaDescriptor.TenantColumnName)
            ],
            TenantColumn  = SchemaDescriptor.TenantColumnName
        };

        var act = () => new RelationValidator().ValidateAndNormalizeRelations(new Struct(), schema);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("is required"));
    }
}
