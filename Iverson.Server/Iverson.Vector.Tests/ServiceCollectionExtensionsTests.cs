using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using Xunit;

namespace Iverson.Vector.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddQdrant_WithApiKey_RegistersResolvableQdrantClient()
    {
        var services = new ServiceCollection();
        services.AddQdrant("localhost", 6334, apiKey: "test-api-key");

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<QdrantClient>();

        client.Should().NotBeNull();
    }
}
