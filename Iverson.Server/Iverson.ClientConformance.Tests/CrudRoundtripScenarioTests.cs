using FluentAssertions;
using Grpc.Net.Client;
using Iverson.Client.Contracts;
using Iverson.ClientConformance;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class CrudRoundtripScenarioTests
{
    private static DriverContext Context() => new(
        Scenario: CrudRoundtripScenario.Name,
        Type: "Widget",
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s1-");

    /// <summary>
    /// Builds a scenario whose collaborators (gRPC client, Postgres probe) are never actually
    /// dialed/queried in this test: an unrecognized language never gets past the register phase,
    /// so nothing downstream of <c>DriverRunner</c> is touched. The channel/connection string are
    /// throwaway values that only need to construct, not connect.
    /// </summary>
    private static CrudRoundtripScenario BuildScenario()
    {
        var channel = GrpcChannel.ForAddress("http://localhost:1");
        var mapping = new ObjectMappingService.ObjectMappingServiceClient(channel);
        var reregistrar = new Reregistrar(mapping);
        var probe = new PostgresProbe("Host=localhost;Database=nonexistent");
        return new CrudRoundtripScenario(new DriverRunner(repoRoot: "/tmp"), mapping, reregistrar, probe);
    }

    [Fact]
    public async Task RunAsync_OnUnrecognizedLanguage_ReportsAFailedCell_NotOk()
    {
        // DriverRunner only knows five languages (dotnet/python/typescript/go/java) — it silently
        // produces no outcome at all for anything else. Without the CrudRoundtripScenario-level
        // guard, that means no Terminal is ever set, no assertion is ever added, and Cell() falls
        // through to ReportCell.Ok for a plain typo like "typescrpt".
        var scenario = BuildScenario();

        var cells = await scenario.RunAsync(["typescrpt"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        var cell = cells[0];
        cell.Language.Should().Be("typescrpt");
        cell.Status.Should().NotBe(CellStatus.Ok);
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("not a recognized conformance driver language");
    }
}
