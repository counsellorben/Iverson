using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using ContractsAuthorizationRules  = Iverson.Client.Contracts.AuthorizationRules;
using ContractsFieldPermission     = Iverson.Client.Contracts.FieldPermission;
using ContractsRelationDescriptor  = Iverson.Client.Contracts.RelationDescriptor;
using ContractsRelationKind        = Iverson.Client.Contracts.RelationKind;
using ContractsRowPermission       = Iverson.Client.Contracts.RowPermission;
using SchemaAuthorizationRules     = Iverson.Api.Schema.AuthorizationRules;
using SchemaFieldPermission        = Iverson.Api.Schema.FieldPermission;
using SchemaRelationDescriptor     = Iverson.Api.Schema.RelationDescriptor;
using SchemaRelationKind           = Iverson.Api.Schema.RelationKind;

namespace Iverson.Api.Tests.Schema;

/// <summary>
/// CSR round 12 §2.2. Registration matches <c>owner_field</c>, <c>field_permissions[].field_name</c>
/// and a relation's <c>foreign_key</c> against the type's columns ignoring case, but used to store
/// the caller's spelling. The write gate looks those names up exactly or lower-first, so an owner
/// field spelled unlike its column beyond the first letter let an owner-scoped writer choose the
/// owner on create and let a bypass writer change it on update. Built and stored descriptors now
/// carry the column's own spelling.
/// </summary>
public class DeclaredNameNormalizationTests
{
    private const string NoteKey = "44444444-0000-0000-0000-000000000004";

    private static IEmbeddingService Embedding()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");
        return embedding;
    }

    // Columns Id (key), Title, OwnerId and AuthorId, plus the server-owned tenant column
    // BuildDescriptor appends.
    private static TypeDescriptor NoteType(string ownerField, params ContractsRowPermission[] rowPermissions)
    {
        var td = new TypeDescriptor { TypeName = "Note" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id",       ObjectType = ObjectType.Guid, IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "Title",    ObjectType = ObjectType.String });
        td.Properties.Add(new PropertyDescriptor { Name = "OwnerId",  ObjectType = ObjectType.String });
        td.Properties.Add(new PropertyDescriptor { Name = "AuthorId", ObjectType = ObjectType.Guid });
        td.Authorization = new ContractsAuthorizationRules { OwnerField = ownerField };
        foreach (var rowPermission in rowPermissions)
            td.Authorization.RowPermissions.Add(rowPermission);
        return td;
    }

    // A descriptor as a build without the rewrite stored it: every declared name keeps the
    // caller's spelling. The OneToMany foreign key happens to match this type's AuthorId column
    // ignoring case, but it names a column of the related type.
    private static SchemaDescriptor StoredNote() => new()
    {
        TypeName      = "Note",
        TableName     = "notes",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Title",    "TEXT", true),
            new ColumnDescriptor("OwnerId",  "TEXT", true),
            new ColumnDescriptor("AuthorId", "UUID", true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ],
        FkColumns     = [new ForeignKeyDescriptor("AuthorId", "Author")],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     =
        [
            new SchemaRelationDescriptor("Author",  SchemaRelationKind.ManyToOne, "Author", "authorid"),
            new SchemaRelationDescriptor("Replies", SchemaRelationKind.OneToMany, "Reply",  "authorid"),
        ],
        Authorization = new SchemaAuthorizationRules(
            "ownerid",
            [],
            [new SchemaFieldPermission("title", ["editors"], [])]),
        TenantColumn  = SchemaDescriptor.TenantColumnName
    };

    // Serializes exactly as SchemaRegistry.RegisterAsync does.
    private static string SerializeAsRegistryWould(SchemaDescriptor descriptor) =>
        System.Text.Json.JsonSerializer.Serialize(
            descriptor,
            new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    private static async Task<SchemaRegistry> LoadedRegistry(SchemaDescriptor stored)
    {
        var repository = Substitute.For<ISchemaRegistryRepository>();
        repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            (stored.TypeName, SerializeAsRegistryWould(stored))
        });
        var registry = new SchemaRegistry(repository, NullLogger<SchemaRegistry>.Instance);
        await registry.LoadAsync();
        return registry;
    }

    private static IReadOnlyCollection<string> Enforce(
        SchemaDescriptor schema, ClaimsPrincipal writer, Struct payload, string? existingRowJson) =>
        AuthorizationFieldMasking.EnforceWriteAuthorization(
            new RowFieldAuthorizationEvaluator(),
            writer,
            schema,
            payload,
            AuthorizationAction.Write,
            "Not authorized to write this entity.",
            existingRowJson,
            requireExistingRow: existingRowJson is not null,
            new AuditLog(NullLogger<AuditLog>.Instance),
            new PayloadSizeValidator());

    [Fact]
    public void BuildDescriptor_RewritesDeclaredNamesToTheMatchedColumnsSpelling()
    {
        var td = NoteType("ownerid");
        td.Authorization.FieldPermissions.Add(
            new ContractsFieldPermission { FieldName = "title", ReadableRoles = { "editors" } });
        td.Relations.Add(new ContractsRelationDescriptor
            { PropertyName = "Author", Kind = ContractsRelationKind.ManyToOne, RelatedType = "Author", ForeignKey = "authorid" });
        td.Relations.Add(new ContractsRelationDescriptor
            { PropertyName = "Replies", Kind = ContractsRelationKind.OneToMany, RelatedType = "Reply", ForeignKey = "authorid" });

        var descriptor = SchemaBuilder.BuildDescriptor(td, Embedding());

        descriptor.Authorization!.OwnerField.Should().Be("OwnerId");
        descriptor.Authorization.FieldPermissions.Single().FieldName.Should().Be("Title");
        descriptor.Relations.Single(r => r.PropertyName == "Author").ForeignKey.Should().Be("AuthorId");
        descriptor.Relations.Single(r => r.PropertyName == "Replies").ForeignKey.Should().Be("authorid",
            "a OneToMany foreign key names a column of the related type, not of this one");
    }

    [Fact]
    public async Task LoadAsync_RewritesAStoredDescriptorsDeclaredNamesToTheMatchedColumnsSpelling()
    {
        var registry = await LoadedRegistry(StoredNote());

        var loaded = registry.Get("Note")!;
        loaded.Authorization!.OwnerField.Should().Be("OwnerId");
        loaded.Authorization.FieldPermissions.Single().FieldName.Should().Be("Title");
        loaded.Relations.Single(r => r.PropertyName == "Author").ForeignKey.Should().Be("AuthorId");
        loaded.Relations.Single(r => r.PropertyName == "Replies").ForeignKey.Should().Be("authorid");
    }

    [Fact]
    public async Task LoadAsync_ANameMatchingCaseDuplicateColumns_IsLeftAsDeclared_AndTheTypeStillLoads()
    {
        // User decision 7: a stored type with case-duplicate columns stays loaded. Its owner field
        // matches two columns, so there is no single spelling to take.
        var stored = StoredNote() with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("Title",    "TEXT", true),
                new ColumnDescriptor("OwnerId",  "TEXT", true),
                new ColumnDescriptor("OWNERID",  "TEXT", true),
                new ColumnDescriptor("AuthorId", "UUID", true),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
            ]
        };

        var registry = await LoadedRegistry(stored);

        registry.IsRegistered("Note").Should().BeTrue();
        registry.Get("Note")!.Authorization!.OwnerField.Should().Be("ownerid");
    }

    [Fact]
    public void Create_ByAnOwnerScopedWriter_StampsTheWriterUnderTheOwnerColumnOnly()
    {
        // No group, so no RowPermission applies: the writer is ownership-scoped and the server
        // must set the owner. The client's own OwnerId must not survive beside it.
        var schema = SchemaBuilder.BuildDescriptor(NoteType("ownerid"), Embedding());
        var payload = new Struct
        {
            Fields = { ["Title"] = Value.ForString("t"), ["OwnerId"] = Value.ForString("someone-else") }
        };

        Enforce(schema, ActingUserFixtures.Principal("writer-1"), payload, existingRowJson: null);

        payload.Fields.Keys.Where(k => string.Equals(k, "OwnerId", StringComparison.OrdinalIgnoreCase))
            .Should().Equal("OwnerId");
        payload.Fields["OwnerId"].StringValue.Should().Be("writer-1");
    }

    [Fact]
    public void Update_ByABypassWriter_ChangingTheOwner_IsRefusedAsImmutable()
    {
        var schema = SchemaBuilder.BuildDescriptor(
            NoteType("ownerid", new ContractsRowPermission { Role = "editors", CanWriteAll = true }), Embedding());
        var payload = new Struct
        {
            Fields =
            {
                ["Id"]      = Value.ForString(NoteKey),
                ["Title"]   = Value.ForString("new"),
                ["OwnerId"] = Value.ForString("writer-1"),
            }
        };

        var act = () => Enforce(schema, ActingUserFixtures.Principal("writer-1", "editors"), payload,
            existingRowJson: $$"""{"Id":"{{NoteKey}}","Title":"old","OwnerId":"someone-else","__TenantId":"test-tenant"}""");

        var ex = act.Should().Throw<RpcException>().Which;
        ex.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Status.Detail.Should().Contain("immutable");
    }
}
