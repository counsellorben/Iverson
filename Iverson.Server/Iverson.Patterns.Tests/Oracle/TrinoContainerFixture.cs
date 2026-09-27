using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Iverson.Patterns.Tests.Oracle;

/// <summary>Pinned: the oracle's semantics are Trino 483's, the version every probe in the spec was run against.</summary>
public static class TrinoImage
{
    public const string Tag = "trinodb/trino:483";
}

/// <summary>ONE Trino container for every oracle test class (a class fixture would start one per class).</summary>
public sealed class TrinoContainerFixture : IAsyncLifetime
{
    private const int HttpPort = 8080;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(TrinoImage.Tag)
        .WithPortBinding(HttpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(HttpPort))
        .Build();

    public TrinoClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Client = new TrinoClient(new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}"));

        // Trino answers HTTP before it can run queries: ready means SELECT 1 returns a row.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            try
            {
                var result = await Client.QueryAsync("SELECT 1");
                if (result.Error is null && result.Rows.Count == 1) return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline) { }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Trino did not become query-ready in 3 minutes.");
            await Task.Delay(2000);
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class TrinoCollection : ICollectionFixture<TrinoContainerFixture>
{
    public const string Name = "trino";
}
