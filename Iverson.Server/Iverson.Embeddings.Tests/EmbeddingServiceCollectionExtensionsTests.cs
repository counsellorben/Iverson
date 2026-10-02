using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iverson.Embeddings.Tests;

public sealed class EmbeddingServiceCollectionExtensionsTests
{
    // Telemetry.HttpClientName is internal and Iverson.Embeddings grants InternalsVisibleTo to nothing,
    // so the name is repeated here; a rename makes the 5-minute test fail.
    private const string HttpClientName = "iverson.embeddings";

    private static HttpClient ResolveEmbeddingClient(params KeyValuePair<string, string?>[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Embeddings:BaseUrl"] = "http://embeddings.test:8091" }
                    .Concat(settings))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmbeddings(config);

        return services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(HttpClientName);
    }

    [Fact]
    public void AddEmbeddings_WithoutConfiguredTimeout_UsesHttpClientsDefaultOf100Seconds()
    {
        using var client = ResolveEmbeddingClient();

        client.Timeout.Should().Be(TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void AddEmbeddings_WithConfiguredTimeout_AppliesItToTheNamedClient()
    {
        using var client = ResolveEmbeddingClient(new KeyValuePair<string, string?>("Embeddings:Timeout", "00:05:00"));

        client.Timeout.Should().Be(TimeSpan.FromMinutes(5));
    }
}
