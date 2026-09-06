using System.Text.Json;
using FluentAssertions;
using Google.Protobuf;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for the descriptor <see cref="Reregistrar"/> actually posts. The RPC itself needs
/// a live channel and is not the interesting half: everything the re-registration DECIDES —
/// the authorization block it stamps on, and the model override <c>ModelRejectedScenario</c>
/// depends on — is decided in <see cref="Reregistrar.BuildDescriptor"/> before the call.
///
/// <para>The model override is graded here rather than only through the scenario because the
/// scenario's own tests drive a SCRIPTED rejection: a <c>Reregistrar</c> that quietly stopped
/// applying the override would provoke no model change on a live stack at all, while every
/// scenario assertion driven from that script stayed green.</para>
/// </summary>
public class ReregistrarTests
{
    private const string DeclaredModel = "nomic-embed-text";

    private static JsonElement DescriptorJson(TypeDescriptor descriptor) =>
        JsonDocument.Parse(JsonFormatter.Default.Format(descriptor)).RootElement.Clone();

    /// <summary>A vector-carrying fixture in the shape T6-T10's drivers register: one embedding
    /// property, one chunked property, both naming the same model, plus a plain scalar.</summary>
    private static JsonElement Fixture() => DescriptorJson(new TypeDescriptor
    {
        TypeName = "S11ModelDotnet",
        Properties =
        {
            new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true },
            new PropertyDescriptor
            {
                Name = "Title", ClrType = ClrType.ClrString,
                IsEmbedding = true, ModelId = DeclaredModel, VectorDim = 768,
            },
            new PropertyDescriptor
            {
                Name = "Body", ClrType = ClrType.ClrString,
                IsChunk = true, ChunkModelId = DeclaredModel, ChunkVectorDim = 768,
            },
            new PropertyDescriptor { Name = "Marker", ClrType = ClrType.ClrString },
        },
    });

    private static PropertyDescriptor Property(TypeDescriptor descriptor, string name) =>
        descriptor.Properties.Single(p => p.Name == name);

    [Fact]
    public void BuildDescriptor_NoModelOverride_LeavesEveryDeclaredModelByteUnchanged()
    {
        var descriptor = Reregistrar.BuildDescriptor(Fixture(), "OwnerId", modelId: null);

        Property(descriptor, "Title").ModelId.Should().Be(DeclaredModel);
        Property(descriptor, "Body").ChunkModelId.Should().Be(DeclaredModel);
    }

    /// <summary>
    /// BOTH fields, not just <c>model_id</c>. The declaration is class-level in every client, so a
    /// descriptor whose embedding property named one model and whose chunk property named another
    /// is a shape no client can produce — and the server's guard reads whichever of the two comes
    /// first, so rewriting only one would provoke the rejection for some fixtures and not others.
    /// </summary>
    [Fact]
    public void BuildDescriptor_ModelOverride_RewritesBothTheEmbeddingAndTheChunkModel()
    {
        var descriptor = Reregistrar.BuildDescriptor(Fixture(), "OwnerId", modelId: "some-other-model");

        Property(descriptor, "Title").ModelId.Should().Be("some-other-model");
        Property(descriptor, "Body").ChunkModelId.Should().Be("some-other-model");
    }

    [Fact]
    public void BuildDescriptor_ModelOverride_LeavesPropertiesThatCarryNoEmbeddingAlone()
    {
        var descriptor = Reregistrar.BuildDescriptor(Fixture(), "OwnerId", modelId: "some-other-model");

        Property(descriptor, "Marker").ModelId.Should().BeEmpty();
        Property(descriptor, "Marker").ChunkModelId.Should().BeEmpty();
        Property(descriptor, "Id").ModelId.Should().BeEmpty();
    }

    /// <summary>
    /// The override must not cost the authorization block the other seven callers re-register FOR:
    /// without it every seeded write is denied.
    /// </summary>
    [Fact]
    public void BuildDescriptor_WithOrWithoutTheOverride_StampsTheAuthorizationRules()
    {
        foreach (var modelId in new string?[] { null, "some-other-model" })
        {
            var descriptor = Reregistrar.BuildDescriptor(Fixture(), "WriterId", modelId);

            descriptor.Authorization.Should().NotBeNull();
            descriptor.Authorization.OwnerField.Should().Be("WriterId");
            descriptor.Authorization.RowPermissions.Should()
                .ContainSingle(p => p.Role == "iverson-loadtest-bypass" && p.CanWriteAll);
        }
    }

    /// <summary>The driver's own descriptor is round-tripped, never rebuilt: the relation shape and
    /// every property the driver reported must survive both paths.</summary>
    [Fact]
    public void BuildDescriptor_ModelOverride_LeavesTheRestOfTheDescriptorIntact()
    {
        var descriptor = Reregistrar.BuildDescriptor(Fixture(), "OwnerId", modelId: "some-other-model");

        descriptor.TypeName.Should().Be("S11ModelDotnet");
        descriptor.Properties.Select(p => p.Name).Should().Equal("Id", "Title", "Body", "Marker");
        Property(descriptor, "Title").VectorDim.Should().Be(768);
        Property(descriptor, "Body").ChunkVectorDim.Should().Be(768);
    }
}
