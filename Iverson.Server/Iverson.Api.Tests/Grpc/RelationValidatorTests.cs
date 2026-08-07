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
    public void ValidateRelations_NestedExistingEntityWithExtraProperties_Throws()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"]   = Value.ForString(Guid.NewGuid().ToString());
        nested.Fields["Name"] = Value.ForString("extra");
        payload.Fields["Author"] = Value.ForStruct(nested);

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("must only include"));
    }

    [Fact]
    public void RelationValidator_ImplementsIRelationValidator()
    {
        typeof(RelationValidator).Should().Implement<IRelationValidator>();
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToOne_ExistingEntityReference_PopulatesForeignKey()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var authorId = Guid.NewGuid().ToString();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(authorId);
        payload.Fields["Author"] = Value.ForStruct(nested);

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields["AuthorId"].StringValue.Should().Be(authorId);
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToOne_KeylessEmbeddedObject_ThrowsAndNamesProperty()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Name"] = Value.ForString("no id here");
        payload.Fields["Author"] = Value.ForStruct(nested);

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("'Author'"));
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToOne_ForeignKeyAlreadyPresent_NavPropertyIgnored()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var fkId = Guid.NewGuid().ToString();
        payload.Fields["AuthorId"] = Value.ForString(fkId);
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Author"] = Value.ForStruct(nested);

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields["AuthorId"].StringValue.Should().Be(fkId);
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToOne_NullFkPlusEmbeddedReference_PopulatesFkWithoutDuplicateKeyCrash()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        payload.Fields["authorId"] = Value.ForNull();
        var authorId = Guid.NewGuid().ToString();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(authorId);
        payload.Fields["Author"] = Value.ForStruct(nested);

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields.Should().ContainKey("AuthorId");
        payload.Fields.Should().NotContainKey("authorId");
        payload.Fields["AuthorId"].StringValue.Should().Be(authorId);

        var act = () => StructSerializer.SerializePayload(payload);
        act.Should().NotThrow();
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
    public void ValidateAndNormalizeRelations_ManyToMany_ListOfReferences_PopulatesForeignKeyListInOrder()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();
        var nested1 = new Struct();
        nested1.Fields["Id"] = Value.ForString(id1);
        var nested2 = new Struct();
        nested2.Fields["Id"] = Value.ForString(id2);
        var navList = new ListValue();
        navList.Values.Add(Value.ForStruct(nested1));
        navList.Values.Add(Value.ForStruct(nested2));
        payload.Fields["Tags"] = Value.ForList(navList.Values.ToArray());

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields["TagIds"].ListValue.Values.Select(v => v.StringValue)
            .Should().Equal(id1, id2);
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToMany_ListContainingKeylessItem_Throws()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var nested1 = new Struct();
        nested1.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        var nested2 = new Struct();
        nested2.Fields["Name"] = Value.ForString("no id");
        var navList = new ListValue();
        navList.Values.Add(Value.ForStruct(nested1));
        navList.Values.Add(Value.ForStruct(nested2));
        payload.Fields["Tags"] = Value.ForList(navList.Values.ToArray());

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>().Where(e => e.Status.Detail.Contains("'Tags[1]'"));
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToOne_ReferenceWithNullSiblings_PopulatesForeignKey()
    {
        // The .NET client serializes every property, so the canonical `post.Author = new Author
        // { Id = id }` arrives with null siblings alongside the key — a key-only Struct built by
        // hand does not exercise this shape.
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var authorId = Guid.NewGuid().ToString();
        var nested = new Struct();
        nested.Fields["id"]   = Value.ForString(authorId);
        nested.Fields["name"] = Value.ForNull();
        nested.Fields["bio"]  = Value.ForNull();
        payload.Fields["Author"] = Value.ForStruct(nested);

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields["AuthorId"].StringValue.Should().Be(authorId);
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToMany_EmptyNavList_ClearsForeignKeyList()
    {
        // Writes are full-document upserts, so an empty nav list deliberately clears the stored
        // FK list. Pinned because it is the one branch that destroys data.
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        payload.Fields["Tags"] = Value.ForList();

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields["TagIds"].ListValue.Values.Should().BeEmpty();
    }

    [Fact]
    public void ValidateAndNormalizeRelations_OneToMany_NavProperty_NoForeignKeyWrittenNoError()
    {
        var schema = MakeSchemaWithRelation(RelationKind.OneToMany, fkNullable: false);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        var navList = new ListValue();
        navList.Values.Add(Value.ForStruct(nested));
        payload.Fields["Author"] = Value.ForList(navList.Values.ToArray());

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().NotThrow();
        payload.Fields.Should().NotContainKey("AuthorId");
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToMany_ScalarInNavList_ThrowsAndWritesNoForeignKey()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Tags"] = Value.ForList(Value.ForStruct(nested), Value.ForString("not-an-object"));

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("'Tags[1]': expected an object, got a scalar"));
        // The one resolvable item must NOT be written on its own — a partial FK list would silently
        // drop the unresolved reference on the full-document upsert.
        payload.Fields.Should().NotContainKey("TagIds");
    }

    [Fact]
    public void ValidateAndNormalizeRelations_ManyToOne_EmbeddedObjectWhenForeignKeyIsNotALocalColumn_Throws()
    {
        // The relation names an FK column this type does not have. Normalizing would inject a
        // phantom key that the stores silently ignore, so it must be an explicit error instead.
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true) with
        {
            ScalarColumns = []
        };
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Author"] = Value.ForStruct(nested);

        var act = () => _sut.ValidateAndNormalizeRelations(payload, schema);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.Detail.Contains("'AuthorId' is not a column on this type"));
        payload.Fields.Should().NotContainKey("AuthorId");
    }

    [Fact]
    public void ManyToOne_NavPropertyStrippedAndForeignKeySurvives()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var authorId = Guid.NewGuid().ToString();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(authorId);
        payload.Fields["Author"] = Value.ForStruct(nested);

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields["AuthorId"].StringValue.Should().Be(authorId);
        payload.Fields.Should().NotContainKey("Author");
    }

    [Fact]
    public void ManyToOne_CamelCaseNavPropertyStripped()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToOne, fkNullable: true);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["author"] = Value.ForStruct(nested);

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields.Should().NotContainKey("author");
        payload.Fields.Should().NotContainKey("Author");
    }

    [Fact]
    public void ManyToMany_NavListStripped()
    {
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Tags"] = Value.ForList(Value.ForStruct(nested));

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields["TagIds"].ListValue.Values.Should().HaveCount(1);
        payload.Fields.Should().NotContainKey("Tags");
    }

    [Fact]
    public void OneToMany_NavPropertyStripped()
    {
        var schema = MakeSchemaWithRelation(RelationKind.OneToMany, fkNullable: false);
        var payload = new Struct();
        var nested = new Struct();
        nested.Fields["Id"] = Value.ForString(Guid.NewGuid().ToString());
        payload.Fields["Author"] = Value.ForList(Value.ForStruct(nested));

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields.Should().NotContainKey("Author");
        payload.Fields.Should().NotContainKey("AuthorId");
    }

    [Fact]
    public void PropertyNameEqualsForeignKey_KeyNotStripped()
    {
        // A7 regression guard: when PropertyName and ForeignKey collide, the nav property IS the
        // FK payload key. Without the name guard, stripping the nav property deletes the FK too.
        var schema = MakeSchemaWithRelation(RelationKind.ManyToMany, fkNullable: true) with
        {
            Relations = [new RelationDescriptor("TagIds", RelationKind.ManyToMany, "Tag", "TagIds")]
        };
        var payload = new Struct();
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();
        payload.Fields["TagIds"] = Value.ForList(Value.ForString(id1), Value.ForString(id2));

        _sut.ValidateAndNormalizeRelations(payload, schema);

        payload.Fields.Should().ContainKey("TagIds");
        payload.Fields["TagIds"].ListValue.Values.Select(v => v.StringValue).Should().Equal(id1, id2);
    }
}
