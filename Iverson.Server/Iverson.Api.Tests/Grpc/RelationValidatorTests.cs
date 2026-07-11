using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Iverson.Sql;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class RelationValidatorTests
{
    private readonly SchemaRegistry _registry;
    private readonly RelationValidator _sut;

    public RelationValidatorTests()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        _registry = new SchemaRegistry(new SchemaRegistryRepository(sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new RelationValidator(_registry);
    }

    private static SchemaDescriptor MakeSchemaWithRelation(RelationKind kind, bool fkNullable) => new()
    {
        TypeName      = "Article",
        TableName     = "articles",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [new ColumnDescriptor("AuthorId", "uuid", fkNullable)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [new RelationDescriptor("Author", kind, "Author", "AuthorId")]
    };

    [Fact]
    public void ValidateRelations_ManyToOne_ValidGuidForeignKey_DoesNotThrow()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        payload.Fields["AuthorId"] = Value.ForString(Guid.NewGuid().ToString());

        var act = () => _sut.ValidateRelations(payload, schema);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRelations_ManyToOne_InvalidGuidForeignKey_Throws()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        payload.Fields["AuthorId"] = Value.ForString("not-a-guid");

        var act = () => _sut.ValidateRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("must be a valid non-empty GUID"));
    }

    [Fact]
    public void ValidateRelations_ManyToOne_MissingRequiredNonNullableForeignKey_Throws()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: false);
        var payload = new Struct();

        var act = () => _sut.ValidateRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("is required"));
    }

    [Fact]
    public void ValidateRelations_ManyToOne_MissingOptionalNullableForeignKey_DoesNotThrow()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();

        var act = () => _sut.ValidateRelations(payload, schema);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRelations_OneToMany_NeverValidated()
    {
        var schema = MakeSchemaWithRelation(RelationKind.OneToMany, fkNullable: false);
        var payload = new Struct();

        var act = () => _sut.ValidateRelations(payload, schema);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRelations_ManyToMany_InvalidGuidInForeignKeyList_Throws()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var list = new ListValue();
        list.Values.Add(Value.ForString("not-a-guid"));
        payload.Fields["TagIds"] = Value.ForList(list.Values.ToArray());

        var act = () => _sut.ValidateRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("invalid GUID"));
    }

    [Fact]
    public void ValidateRelations_NestedExistingEntityWithExtraProperties_Throws()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"]   = Value.ForString(Guid.NewGuid().ToString());
        nested.Fields["Name"] = Value.ForString("extra");
        payload.Fields["Author"] = Value.ForStruct(nested);

        var act = () => _sut.ValidateRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("must only include"));
    }

    [Fact]
    public void RelationValidator_ImplementsIRelationValidator()
    {
        typeof(RelationValidator).Should().Implement<IRelationValidator>();
    }
}
