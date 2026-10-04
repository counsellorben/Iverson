using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Sql.Tests;

/// <summary>
/// Exercises <see cref="TokenRevocationRepository"/> against a real Postgres instance: its DDL,
/// its upsert and its <c>timestamptz</c> read are all SQL that a substituted
/// <see cref="IRecordStoreQueryExecutor"/> never runs (see
/// <see cref="TenantRepositoryPostgresContainerFixture"/> for the <c>timestamptz</c>/Dapper defect
/// class a mocked test cannot see).
/// </summary>
public sealed class TokenRevocationRepositoryPostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public PostgresRepository Repository { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Repository = new PostgresRepository(
            _container.GetConnectionString(),
            NullLogger<PostgresRepository>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class TokenRevocationRepositoryPostgresIntegrationTests(TokenRevocationRepositoryPostgresContainerFixture fixture)
    : IClassFixture<TokenRevocationRepositoryPostgresContainerFixture>
{
    private readonly PostgresRepository _sql = fixture.Repository;

    private async Task<TokenRevocationRepository> FreshRepositoryAsync()
    {
        await _sql.ExecuteAsync("DROP TABLE IF EXISTS iverson_token_revocations");
        var repo = new TokenRevocationRepository(_sql);
        await repo.EnsureTableAsync();
        return repo;
    }

    [Fact]
    public async Task EnsureTableAsync_CreatesAnEmptyTable_AndIsIdempotent()
    {
        var repo = await FreshRepositoryAsync();

        await repo.EnsureTableAsync();

        (await repo.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeAsync_ThenListAsync_ReturnsTheSubWithTheDatabaseTime()
    {
        var repo = await FreshRepositoryAsync();

        await repo.RevokeAsync("user-uid-1");

        var rows = (await repo.ListAsync()).ToList();
        rows.Should().ContainSingle();
        rows[0].Sub.Should().Be("user-uid-1");
        rows[0].RevokedAt.Offset.Should().Be(TimeSpan.Zero);
        rows[0].RevokedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task RevokeAsync_SubAlreadyRevoked_MovesRevokedAtForwardInPlace()
    {
        var repo = await FreshRepositoryAsync();
        await _sql.ExecuteAsync(
            "INSERT INTO iverson_token_revocations (sub, revoked_at) VALUES ('user-uid-1', '2000-01-01T00:00:00Z')");

        await repo.RevokeAsync("user-uid-1");

        var rows = (await repo.ListAsync()).ToList();
        rows.Should().ContainSingle();
        rows[0].RevokedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ListAsync_ReturnsEveryRevokedSub()
    {
        var repo = await FreshRepositoryAsync();

        await repo.RevokeAsync("user-uid-1");
        await repo.RevokeAsync("user-uid-2");

        (await repo.ListAsync()).Select(r => r.Sub).Should().BeEquivalentTo("user-uid-1", "user-uid-2");
    }
}
