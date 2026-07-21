using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Sql;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

public class TenantStatusCacheTests
{
    private readonly ITenantRepository _repository;
    private readonly IMemoryCache _cache;
    private readonly TenantStatusCache _sut;

    public TenantStatusCacheTests()
    {
        _repository = Substitute.For<ITenantRepository>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _sut = new TenantStatusCache(_repository, _cache);
    }

    [Fact]
    public async Task GetStatusAsync_CacheMiss_ActiveTenant_ReturnsStatusAndQueriesRepository()
    {
        _repository.GetAsync("tenant-a")
            .Returns(new TenantRow("tenant-a", "Tenant A", "active", DateTimeOffset.UtcNow));

        var status = await _sut.GetStatusAsync("tenant-a");

        status.Should().Be("active");
        await _repository.Received(1).GetAsync("tenant-a");
    }

    [Fact]
    public async Task GetStatusAsync_CacheMiss_SuspendedTenant_ReturnsSuspended()
    {
        _repository.GetAsync("tenant-b")
            .Returns(new TenantRow("tenant-b", "Tenant B", "suspended", DateTimeOffset.UtcNow));

        var status = await _sut.GetStatusAsync("tenant-b");

        status.Should().Be("suspended");
    }

    [Fact]
    public async Task GetStatusAsync_CacheMiss_UnknownTenant_ReturnsNull()
    {
        _repository.GetAsync("tenant-missing").Returns((TenantRow?)null);

        var status = await _sut.GetStatusAsync("tenant-missing");

        status.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_CacheHit_DoesNotQueryRepositoryAgain()
    {
        _repository.GetAsync("tenant-c")
            .Returns(new TenantRow("tenant-c", "Tenant C", "active", DateTimeOffset.UtcNow));

        var first = await _sut.GetStatusAsync("tenant-c");
        var second = await _sut.GetStatusAsync("tenant-c");

        first.Should().Be("active");
        second.Should().Be("active");
        await _repository.Received(1).GetAsync("tenant-c");
    }

    [Fact]
    public async Task GetStatusAsync_CacheHit_NullStatusIsAlsoCached()
    {
        _repository.GetAsync("tenant-missing").Returns((TenantRow?)null);

        var first = await _sut.GetStatusAsync("tenant-missing");
        var second = await _sut.GetStatusAsync("tenant-missing");

        first.Should().BeNull();
        second.Should().BeNull();
        await _repository.Received(1).GetAsync("tenant-missing");
    }
}
