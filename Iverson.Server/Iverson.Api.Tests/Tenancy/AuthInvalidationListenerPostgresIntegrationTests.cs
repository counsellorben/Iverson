using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Sql;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// CSR round 12 §2.3 against a real Postgres, with the worker's startup run as Program.cs runs it
/// (<see cref="ApiLoginGrantsPostgresContainerFixture"/>). "This instance" is a listener with its own real
/// caches over the api's login; "another instance" is a second set of repositories on their own
/// connections. The caches' 30 s TTL is the only other way a change could show, so every wait below is
/// far shorter than it.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class AuthInvalidationListenerPostgresIntegrationTests(ApiLoginGrantsPostgresContainerFixture fixture)
    : IClassFixture<ApiLoginGrantsPostgresContainerFixture>
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset IssuedBeforeTheTest = DateTimeOffset.UtcNow.AddMinutes(-1);

    /// <summary>The real cache, counting the drops, so a test can wait for the listener.</summary>
    private sealed class CountingTokenRevocationCache(ITokenRevocationCache inner) : ITokenRevocationCache
    {
        private int _invalidations;

        public int Invalidations => Volatile.Read(ref _invalidations);

        public Task<bool> IsRevokedAsync(string sub, DateTimeOffset? issuedAt, CancellationToken cancellationToken = default) =>
            inner.IsRevokedAsync(sub, issuedAt, cancellationToken);

        public void Invalidate()
        {
            Interlocked.Increment(ref _invalidations);
            inner.Invalidate();
        }
    }

    private sealed record ThisInstance(
        CountingTokenRevocationCache Revocations,
        TenantStatusCache TenantStatuses,
        AuthInvalidationListener Listener,
        string ApplicationName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Listener.StopAsync(CancellationToken.None);
            Listener.Dispose();
        }
    }

    private async Task<ThisInstance> StartThisInstanceAsync()
    {
        var applicationName = "listener-" + Guid.NewGuid().ToString("N")[..12];
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    new NpgsqlConnectionStringBuilder(fixture.ApiConnectionString) { ApplicationName = applicationName }.ConnectionString
            })
            .Build();
        var revocations = new CountingTokenRevocationCache(
            new TokenRevocationCache(new TokenRevocationRepository(fixture.Api), TimeProvider.System));
        var tenantStatuses = new TenantStatusCache(
            new TenantRepository(TenantSchema.TableName, fixture.Api), new MemoryCache(new MemoryCacheOptions()));
        var listener = new AuthInvalidationListener(
            configuration, revocations, tenantStatuses, NullLogger<AuthInvalidationListener>.Instance);

        await listener.StartAsync(CancellationToken.None);

        (await WaitUntilAsync(() => Task.FromResult(revocations.Invalidations >= 1), Soon))
            .Should().BeTrue("the listener drops both caches once its LISTEN has taken effect");
        return new ThisInstance(revocations, tenantStatuses, listener, applicationName);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(100);
        }
        return await condition();
    }

    private static string NewSub() => "sub-" + Guid.NewGuid().ToString("N");

    private static string NewTenantId() => "tenant_" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task ARevocationByAnotherInstance_ReachesThisInstancesCache_WithinSeconds()
    {
        await using var thisInstance = await StartThisInstanceAsync();
        var sub = NewSub();
        (await thisInstance.Revocations.IsRevokedAsync(sub, IssuedBeforeTheTest)).Should().BeFalse();

        await new TokenRevocationRepository(fixture.Api).RevokeAsync(sub);

        (await WaitUntilAsync(() => thisInstance.Revocations.IsRevokedAsync(sub, IssuedBeforeTheTest), Soon))
            .Should().BeTrue("the revocation's notification drops this instance's snapshot");
    }

    [Fact]
    public async Task ATenantStatusWriteByAnotherInstance_ReachesThisInstancesCache_WithinSeconds()
    {
        await using var thisInstance = await StartThisInstanceAsync();
        var tenantId = NewTenantId();
        var anotherInstance = new TenantRepository(TenantSchema.TableName, fixture.Api);
        await anotherInstance.InsertAsync(tenantId, "Listener probe", "active");
        (await WaitUntilAsync(async () => await thisInstance.TenantStatuses.GetStatusAsync(tenantId) == "active", Soon))
            .Should().BeTrue();

        (await anotherInstance.UpdateStatusUnlessDeletedAsync(tenantId, "suspended")).Should().BeTrue();

        (await WaitUntilAsync(async () => await thisInstance.TenantStatuses.GetStatusAsync(tenantId) == "suspended", Soon))
            .Should().BeTrue("the suspension's notification drops this instance's cached status");
    }

    [Fact]
    public async Task TheWorkersLegacyTenantSeed_ReachesThisInstancesCachedMiss_WithinSeconds()
    {
        await using var thisInstance = await StartThisInstanceAsync();
        var tenantId = NewTenantId();
        (await thisInstance.TenantStatuses.GetStatusAsync(tenantId)).Should().BeNull("an unknown tenant's miss is cached");

        // The worker seeds as the owner (Program.cs's SeedIfMissingAsync loop).
        await new TenantRepository(TenantSchema.TableName, fixture.Owner).SeedIfMissingAsync(tenantId, tenantId, "active");

        (await WaitUntilAsync(async () => await thisInstance.TenantStatuses.GetStatusAsync(tenantId) == "active", Soon))
            .Should().BeTrue("the seed's notification drops the cached miss");
    }

    [Fact]
    public async Task AReconnect_DropsBothCaches()
    {
        await using var thisInstance = await StartThisInstanceAsync();
        var sub = NewSub();
        var tenantId = NewTenantId();
        (await thisInstance.Revocations.IsRevokedAsync(sub, IssuedBeforeTheTest)).Should().BeFalse();
        (await thisInstance.TenantStatuses.GetStatusAsync(tenantId)).Should().BeNull();

        // Written with no notification, as no api write is: only a cache drop shows them before the TTL.
        await fixture.Owner.ExecuteAsync(
            "INSERT INTO iverson_token_revocations (sub, revoked_at) VALUES (@Sub, now())", new { Sub = sub });
        await fixture.Owner.ExecuteAsync(
            $"""INSERT INTO "{TenantSchema.TableName}" ("Id", "DisplayName", "Status", "CreatedAt") VALUES (@Id, @Id, 'active', now())""",
            new { Id = tenantId });
        (await thisInstance.Revocations.IsRevokedAsync(sub, IssuedBeforeTheTest)).Should().BeFalse("nothing has dropped the snapshot yet");

        await fixture.Owner.ExecuteAsync(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = @Name",
            new { Name = thisInstance.ApplicationName });

        (await WaitUntilAsync(() => Task.FromResult(thisInstance.Revocations.Invalidations >= 2), TimeSpan.FromSeconds(15)))
            .Should().BeTrue("the listener reconnects after 1 s and drops both caches once it is listening again");
        (await thisInstance.Revocations.IsRevokedAsync(sub, IssuedBeforeTheTest)).Should().BeTrue();
        (await thisInstance.TenantStatuses.GetStatusAsync(tenantId)).Should().Be("active");
    }
}
