using System.Net;
using System.Security.Cryptography;
using System.Text;
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

    // Nothing else ties the composite to the assemblies map: an implementation that
    // hashed only the entry assembly (Iverson.Api) would still return a full, correct
    // assemblies map and would pass every other test on this branch. Recomputing the
    // expected composite from the returned assemblies map, using the same algorithm
    // BuildIdentity.Compute uses, catches that specific failure.
    [Fact]
    public void Compute_CompositeIsDerivedFromEveryAssemblyInTheMap()
    {
        var (composite, assemblies) = BuildIdentity.Compute();

        // Sanity: guard against a degenerate single-entry map making the recomputation
        // below vacuously true.
        assemblies.Count.Should().BeGreaterThan(1);

        var sb = new StringBuilder();
        foreach (var (name, mvid) in assemblies)
            sb.Append(name).Append(':').Append(mvid).Append('\n');

        var expectedComposite = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]
            .ToLowerInvariant();

        composite.Should().Be(expectedComposite);
    }
}
