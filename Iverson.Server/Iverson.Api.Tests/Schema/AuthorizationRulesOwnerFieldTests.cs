using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using NSubstitute;
using Xunit;
using ContractsAuthorizationRules = Iverson.Client.Contracts.AuthorizationRules;
using ContractsRowPermission      = Iverson.Client.Contracts.RowPermission;
using SchemaAuthorizationRules    = Iverson.Api.Schema.AuthorizationRules;
using SchemaFieldPermission       = Iverson.Api.Schema.FieldPermission;
using SchemaRowPermission         = Iverson.Api.Schema.RowPermission;

namespace Iverson.Api.Tests.Schema;

/// <summary>
/// object_mapping.proto declares <c>owner_field</c> as a plain string whose EMPTY value means
/// "no ownership dimension"; proto3 never yields null for it, so a bypass-only rule set arrives
/// at <c>SchemaBuilder.BuildDescriptor</c> with <c>OwnerField == ""</c> and is persisted that way
/// in <c>_iverson_schema</c>. Every reader in Iverson.Api branches on <c>is not null</c>, and both
/// store consumers then index <c>propertyName[0]</c> — the IndexOutOfRangeException that DLQ'd
/// every write to such a type (Phase 2 Task 9′, EnrichSmoke). The record is the one place where
/// registration, rehydration through System.Text.Json, and hand construction all meet, so it
/// normalizes the empty string to null itself and every reader sees one shape.
/// </summary>
public class AuthorizationRulesOwnerFieldTests
{
    private static List<SchemaRowPermission> Bypass() => [new("bypass", true, true, true)];

    [Fact]
    public void Constructor_NormalizesAnEmptyOwnerFieldToNull()
    {
        var rules = new SchemaAuthorizationRules("", Bypass(), new List<SchemaFieldPermission>());

        rules.OwnerField.Should().BeNull();
    }

    [Fact]
    public void Constructor_KeepsANonEmptyOwnerField()
    {
        var rules = new SchemaAuthorizationRules("OwnerId", Bypass(), new List<SchemaFieldPermission>());

        rules.OwnerField.Should().Be("OwnerId");
    }

    [Fact]
    public void With_NormalizesAnEmptyOwnerFieldToNull()
    {
        // A with-expression bypasses the property initializer and goes through the init accessor.
        var rules = new SchemaAuthorizationRules("OwnerId", Bypass(), new List<SchemaFieldPermission>());

        var cleared = rules with { OwnerField = "" };

        cleared.OwnerField.Should().BeNull();
    }

    [Fact]
    public void Deserialize_NormalizesAnEmptyOwnerFieldToNull_AsSchemaRegistryLoadAsyncReadsIt()
    {
        // Exactly the JSON shape SchemaRegistry persists for a bypass-only rule set registered
        // before this fix; LoadAsync rehydrates it through System.Text.Json, not the constructor
        // call sites, so the normalization must survive that path too.
        const string json =
            """{"ownerField":"","rowPermissions":[{"role":"bypass","canReadAll":true,"canWriteAll":true,"canDeleteAll":true}],"fieldPermissions":[]}""";

        var rules = JsonSerializer.Deserialize<SchemaAuthorizationRules>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        });

        rules.Should().NotBeNull();
        rules!.OwnerField.Should().BeNull();
        rules.RowPermissions.Should().ContainSingle(p => p.Role == "bypass");
    }

    [Fact]
    public void BuildDescriptor_MapsTheProtoDefaultEmptyOwnerFieldToNull()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor
        {
            TypeName   = "Article",
            Properties = { new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true } },
            // No owner_field set: proto3 reports "" here, never null.
            Authorization = new ContractsAuthorizationRules
            {
                RowPermissions = { new ContractsRowPermission { Role = "bypass", CanReadAll = true, CanWriteAll = true, CanDeleteAll = true } }
            }
        };
        typeDesc.Authorization.OwnerField.Should().BeEmpty("proto3 strings default to empty, which is the premise of this test");

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.Authorization.Should().NotBeNull();
        descriptor.Authorization!.OwnerField.Should().BeNull();
    }
}
