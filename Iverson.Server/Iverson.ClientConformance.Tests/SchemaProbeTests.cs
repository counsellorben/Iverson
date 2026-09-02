using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// <see cref="SchemaProbe"/>'s read needs a live Postgres, so what is gradable here is its FAILURE
/// contract — and that contract is load-bearing. <c>ModelRejectedScenario</c> treats a null return
/// as "this type is registered but carries no embedded content", which is a real and legitimate
/// state; if an unreachable database produced the same null, an entire conformance run against a
/// dead connection string would report five fixtures that simply carry no model, and the parity
/// assertion's detail would say so rather than naming the connection failure.
/// </summary>
public class SchemaProbeTests
{
    /// <summary>The harness's own copy of the server's registry table name — the same
    /// keep-a-separate-copy rule <see cref="PostgresProbe.TableName"/> follows, and the value
    /// <c>ModelRejectedScenario</c> builds its <c>DELETE FROM</c> assertion out of.</summary>
    [Fact]
    public void SchemaTable_IsTheServersSchemaRegistryTable()
    {
        SchemaProbe.SchemaTable.Should().Be("_iverson_schema");
    }

    [Fact]
    public async Task FetchModelAsync_WithAnUnusableConnectionString_ThrowsRatherThanReportingNoModel()
    {
        var probe = new SchemaProbe(string.Empty);

        var read = async () => await probe.FetchModelAsync("S11ModelDotnet");

        await read.Should().ThrowAsync<Exception>(
            "a failed read must be distinguishable from a type that legitimately carries no "
            + "embedding model, which is what a null return means");
    }
}
