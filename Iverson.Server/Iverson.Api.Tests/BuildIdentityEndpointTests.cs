using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Xunit;

namespace Iverson.Api.Tests;

// Integration-level coverage for the /build endpoint: it identifies the code actually
// running, so a benchmark run can be attributed to it. A stale iverson-api image, built
// from a checkout that no longer existed, once served two benchmark runs undetected.
public class BuildIdentityEndpointTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public BuildIdentityEndpointTests(AuthTestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AnonymousGet_Build_ReturnsCompositeAndAssemblies()
    {
        var response = await _client.GetAsync("/build");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var composite = root.GetProperty("composite").GetString();
        composite.Should().MatchRegex(new Regex("^[0-9a-f]{16}$"));

        var assemblies = root.GetProperty("assemblies");
        var names = assemblies.EnumerateObject().Select(p => p.Name).ToList();

        // Assert containment, never an exact count: in the test host AppContext.BaseDirectory
        // is the test project's output directory, which also holds Iverson.Api.Tests.dll —
        // itself matching Iverson.*.dll. The map has 8 entries there and 7 in the container.
        names.Should().Contain(new[]
        {
            "Iverson.Api",
            "Iverson.Client.Contracts",
            "Iverson.Embeddings",
            "Iverson.Events",
            "Iverson.Sql",
            "Iverson.StarRocks",
            "Iverson.Vector",
        });
    }
}
