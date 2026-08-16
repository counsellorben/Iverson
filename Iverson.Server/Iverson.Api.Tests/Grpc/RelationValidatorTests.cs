using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class RelationValidatorTests
{
    private readonly RelationValidator _sut;

    public RelationValidatorTests()
    {
        _sut = new RelationValidator();
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

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRelations_ManyToOne_InvalidGuidForeignKey_Throws()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        payload.Fields["AuthorId"] = Value.ForString("not-a-guid");

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("must be a valid non-empty GUID"));
    }

    [Fact]
    public void ValidateRelations_ManyToOne_MissingRequiredNonNullableForeignKey_Throws()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: false);
        var payload = new Struct();

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("is required"));
    }

    [Fact]
    public void ValidateRelations_ManyToOne_MissingOptionalNullableForeignKey_DoesNotThrow()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRelations_OneToMany_NeverValidated()
    {
        var schema = MakeSchemaWithRelation(RelationKind.OneToMany, fkNullable: false);
        var payload = new Struct();

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

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

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("invalid GUID"));
    }

    [Fact]
    public void RelationValidator_ImplementsIRelationValidator()
    {
        typeof(RelationValidator).Should().Implement<IRelationValidator>();
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToOne_NullFkNullableColumnNoNav_DoesNotThrow()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        payload.Fields["authorId"] = Value.ForNull();

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().NotThrow();
    }

    [Fact]
    public void PropertyNameEqualsForeignKey_KeyNotStripped()
    {
        // A7 regression guard, reversed by the IVC-REL-003 ruling: a PropertyName/ForeignKey
        // collision is now rejected at registration (SchemaRegistrationOrchestrator), so this
        // schema shape can no longer reach RelationValidator in production. The descriptor is
        // still buildable directly in a unit test, and RelationValidator's own collision
        // tolerance is gone — the same payload key is now read as a nav property and rejected.
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("TagIds", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();
        payload.Fields["TagIds"] = Value.ForList(Value.ForString(id1), Value.ForString(id2));

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>();
    }

    [Fact]
    public void ManyToMany_PropertyNameEqualsForeignKey_NoConflictError()
    {
        // A7 collision schema, reversed by the IVC-REL-003 ruling: PropertyName and ForeignKey
        // colliding is no longer tolerated — the server rejects this shape at registration, and
        // RelationValidator rejects it too if it is ever handed such a descriptor directly.
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("TagIds", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();
        payload.Fields["TagIds"] = Value.ForList(Value.ForString(id1), Value.ForString(id2));

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>();
    }

    [Fact]
    public void ManyToOne_NavPropertyPresent_RejectsNamingNavAndForeignKey()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Author"] = Value.ForStruct(nested);

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("'Author'") && e.Status.Detail.Contains("'AuthorId'"));
    }

    [Fact]
    public void OneToOne_NavPropertyPresent_RejectsNamingNavAndForeignKey()
    {
        var schema = MakeSchemaWithRelation(RelationKind.OneToOne, fkNullable: true);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Author"] = Value.ForStruct(nested);

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("'Author'") && e.Status.Detail.Contains("'AuthorId'"));
    }

    [Fact]
    public void ManyToMany_NavListPresent_RejectsNamingNavAndForeignKey()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Tags"] = Value.ForList(Value.ForStruct(nested));

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("'Tags'") && e.Status.Detail.Contains("'TagIds'"));
    }

    [Fact]
    public void OneToMany_NavPropertyPresent_RejectsNamingNavAndForeignKey()
    {
        var schema = MakeSchemaWithRelation(RelationKind.OneToMany, fkNullable: false);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Author"] = Value.ForList(Value.ForStruct(nested));

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        // A OneToMany payload carries no key at all — the FK is a column on the related
        // entity's row — so the message must not tell the caller to "send 'AuthorId'".
        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("'Author'")
                     && e.Status.Detail.Contains("set 'AuthorId' on each related Author instead.")
                     && !e.Status.Detail.Contains("send 'AuthorId'"));
    }

    [Fact]
    public void ManyToOne_CamelCaseNavPropertyPresent_Rejects()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["author"] = Value.ForStruct(nested);

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("'Author'") && e.Status.Detail.Contains("'AuthorId'"));
    }

    [Fact]
    public void ManyToOne_NullValueNavProperty_Tolerated()
    {
        // .NET and Java serialize every property, so an unset nav member arrives as
        // `Author: null`. This must be treated as ABSENT, not rejected.
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        payload.Fields["AuthorId"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Author"]   = Value.ForNull();

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().NotThrow();
    }
}
