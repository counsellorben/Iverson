using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class PhaseDocumentTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserialize_WriteDocumentWithKeys_PopulatesKeysMap()
    {
        var json = """
            {
              "language": "python",
              "phase": "write",
              "steps": [
                { "name": "create-primary", "ok": true, "keys": { "primary": "6b1f1a0e-1111-4a2b-9c3d-000000000001" } }
              ]
            }
            """;

        var document = JsonSerializer.Deserialize<PhaseDocument>(json, Options);

        document.Should().NotBeNull();
        document!.Language.Should().Be("python");
        document.Phase.Should().Be("write");
        document.Steps.Should().ContainSingle();
        document.Steps[0].Ok.Should().BeTrue();
        document.Steps[0].Keys.Should().ContainKey("primary")
            .WhoseValue.Should().Be("6b1f1a0e-1111-4a2b-9c3d-000000000001");
    }

    [Fact]
    public void Deserialize_FailedStep_ReportsOkFalseAndError_WithoutThrowing()
    {
        var json = """
            {
              "language": "go",
              "phase": "update",
              "steps": [
                { "name": "update-primary", "ok": false, "error": "server returned 5 NOT_FOUND: row does not exist" }
              ]
            }
            """;

        var document = JsonSerializer.Deserialize<PhaseDocument>(json, Options);

        document.Should().NotBeNull();
        document!.Steps.Should().ContainSingle();
        var step = document.Steps[0];
        step.Ok.Should().BeFalse();
        step.Error.Should().Be("server returned 5 NOT_FOUND: row does not exist");
        // A failed step is data, not an exception: the driver exited 0 and reported this.
        step.TypeDescriptor.Should().BeNull();
        step.Keys.Should().BeNull();
        step.Entity.Should().BeNull();
    }

    [Fact]
    public void Deserialize_RegisterDocument_PopulatesTypeDescriptorAsRawJson()
    {
        var json = """
            {
              "language": "dotnet",
              "phase": "register",
              "steps": [
                { "name": "register-widget", "ok": true, "typeDescriptor": { "typeName": "Widget", "properties": [] } }
              ]
            }
            """;

        var document = JsonSerializer.Deserialize<PhaseDocument>(json, Options);

        document!.Steps[0].TypeDescriptor.Should().NotBeNull();
        document.Steps[0].TypeDescriptor!.Value.GetProperty("typeName").GetString().Should().Be("Widget");
    }

    [Fact]
    public void Deserialize_ReadDocument_PopulatesEntityAsRawJson()
    {
        var json = """
            {
              "language": "java",
              "phase": "read",
              "steps": [
                { "name": "read-primary", "ok": true, "entity": { "id": "abc", "name": "widget-1" } }
              ]
            }
            """;

        var document = JsonSerializer.Deserialize<PhaseDocument>(json, Options);

        document!.Steps[0].Entity!.Value.GetProperty("name").GetString().Should().Be("widget-1");
    }

    [Fact]
    public void PhaseNames_RoundTripAllFivePhases()
    {
        PhaseNames.ToToken(Phase.Register).Should().Be("register");
        PhaseNames.ToToken(Phase.Write).Should().Be("write");
        PhaseNames.ToToken(Phase.Read).Should().Be("read");
        PhaseNames.ToToken(Phase.Update).Should().Be("update");
        PhaseNames.ToToken(Phase.Delete).Should().Be("delete");
    }
}
