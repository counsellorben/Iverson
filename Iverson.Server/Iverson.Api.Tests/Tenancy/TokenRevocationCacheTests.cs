using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Sql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

public class TokenRevocationCacheTests
{
    private static readonly DateTimeOffset RevokedAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly ITokenRevocationRepository _repository = Substitute.For<ITokenRevocationRepository>();
    private readonly ManualTimeProvider _time = new(RevokedAt.AddMinutes(5));
    private readonly TokenRevocationCache _sut;

    public TokenRevocationCacheTests()
    {
        _repository.ListAsync().Returns(Rows(("revoked-sub", RevokedAt)));
        _sut = new TokenRevocationCache(_repository, _time);
    }

    private static IEnumerable<(string Sub, DateTimeOffset RevokedAt)> Rows(
        params (string Sub, DateTimeOffset RevokedAt)[] rows) => rows;

    /// <summary>A clock that only moves when told to.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task IsRevokedAsync_IssuedBeforeRevocation_IsRevoked()
    {
        (await _sut.IsRevokedAsync("revoked-sub", RevokedAt.AddMinutes(-10))).Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_IssuedAtTheRevocationInstant_IsRevoked()
    {
        (await _sut.IsRevokedAsync("revoked-sub", RevokedAt)).Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_IssuedAfterRevocation_IsNotRevoked()
    {
        (await _sut.IsRevokedAsync("revoked-sub", RevokedAt.AddSeconds(1))).Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_MissingIatWithRevokedSub_IsRevoked()
    {
        (await _sut.IsRevokedAsync("revoked-sub", issuedAt: null)).Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_UnknownSub_IsNotRevoked()
    {
        (await _sut.IsRevokedAsync("someone-else", RevokedAt.AddMinutes(-10))).Should().BeFalse();
        (await _sut.IsRevokedAsync("someone-else", issuedAt: null)).Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_WithinThirtySeconds_ServesTheSnapshotWithoutReloading()
    {
        await _sut.IsRevokedAsync("revoked-sub", null);
        _repository.ListAsync().Returns(Rows());
        _time.Advance(TimeSpan.FromSeconds(29));

        (await _sut.IsRevokedAsync("revoked-sub", null)).Should().BeTrue();
        await _repository.Received(1).ListAsync();
    }

    // The rule is "more than 30 s": a snapshot exactly 30 s old is still served.
    [Fact]
    public async Task IsRevokedAsync_AtExactlyThirtySeconds_ServesTheSnapshotWithoutReloading()
    {
        await _sut.IsRevokedAsync("revoked-sub", null);
        _time.Advance(TimeSpan.FromSeconds(30));

        await _sut.IsRevokedAsync("revoked-sub", null);

        await _repository.Received(1).ListAsync();
    }

    [Fact]
    public async Task IsRevokedAsync_AfterThirtySeconds_ReloadsAndSeesANewRevocation()
    {
        (await _sut.IsRevokedAsync("newly-revoked-sub", null)).Should().BeFalse();
        _repository.ListAsync().Returns(Rows(("revoked-sub", RevokedAt), ("newly-revoked-sub", RevokedAt.AddMinutes(1))));
        _time.Advance(TimeSpan.FromSeconds(31));

        (await _sut.IsRevokedAsync("newly-revoked-sub", null)).Should().BeTrue();
        await _repository.Received(2).ListAsync();
    }

    [Fact]
    public async Task IsRevokedAsync_ConcurrentCallers_TriggerOneReload()
    {
        var pending = new TaskCompletionSource<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>>();
        _repository.ListAsync().Returns(pending.Task);

        var callers = Enumerable.Range(0, 8)
            .Select(_ => _sut.IsRevokedAsync("revoked-sub", null))
            .ToList();
        pending.SetResult(Rows(("revoked-sub", RevokedAt)));
        var results = await Task.WhenAll(callers);

        results.Should().AllSatisfy(revoked => revoked.Should().BeTrue());
        await _repository.Received(1).ListAsync();
    }

    // A shared reload that fails fails every caller waiting on it, and is not replayed: the next
    // call queries Postgres again.
    [Fact]
    public async Task IsRevokedAsync_SharedReloadFails_FailsEveryWaiterAndTheNextCallReloads()
    {
        var pending = new TaskCompletionSource<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>>();
        _repository.ListAsync().Returns(pending.Task);

        var callers = Enumerable.Range(0, 4)
            .Select(_ => _sut.IsRevokedAsync("revoked-sub", null))
            .ToList();
        pending.SetException(new InvalidOperationException("postgres is down"));

        foreach (var caller in callers)
            await caller.Invoking(async c => await c)
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("postgres is down");
        await _repository.Received(1).ListAsync();

        _repository.ListAsync().Returns(Rows(("revoked-sub", RevokedAt)));
        (await _sut.IsRevokedAsync("revoked-sub", null)).Should().BeTrue();
        await _repository.Received(2).ListAsync();
    }

    // A cancelled request stops waiting; the reload it joined still completes for everyone else.
    [Fact]
    public async Task IsRevokedAsync_CancelledWaiter_ThrowsWhileTheSharedReloadCompletesForOthers()
    {
        var pending = new TaskCompletionSource<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>>();
        _repository.ListAsync().Returns(pending.Task);
        using var cts = new CancellationTokenSource();

        var cancelled = _sut.IsRevokedAsync("revoked-sub", null, cts.Token);
        var other = _sut.IsRevokedAsync("revoked-sub", null);
        cts.Cancel();

        // Bounded so a waiter that ignores its token fails here (TimeoutException) rather than hangs.
        await cancelled.Invoking(async c => await c.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        other.IsCompleted.Should().BeFalse();
        pending.SetResult(Rows(("revoked-sub", RevokedAt)));
        (await other).Should().BeTrue();
        await _repository.Received(1).ListAsync();
    }

    // The constructor's ListAsync stub completes synchronously. A reload that completed before
    // it was recorded as in flight must not stay cached and block every later reload.
    [Fact]
    public async Task IsRevokedAsync_SynchronousRepository_ReloadsOncePerExpiry()
    {
        await _sut.IsRevokedAsync("revoked-sub", null);

        for (var expiry = 1; expiry <= 3; expiry++)
        {
            _time.Advance(TimeSpan.FromSeconds(31));
            await _sut.IsRevokedAsync("revoked-sub", null);
            await _sut.IsRevokedAsync("revoked-sub", null);

            await _repository.Received(1 + expiry).ListAsync();
        }
    }

    [Fact]
    public async Task Invalidate_WithinThirtySeconds_ReloadsOnTheNextCall()
    {
        (await _sut.IsRevokedAsync("newly-revoked-sub", null)).Should().BeFalse();
        _repository.ListAsync().Returns(Rows(("revoked-sub", RevokedAt), ("newly-revoked-sub", RevokedAt.AddMinutes(1))));
        _time.Advance(TimeSpan.FromSeconds(1));

        _sut.Invalidate();

        (await _sut.IsRevokedAsync("newly-revoked-sub", null)).Should().BeTrue();
        await _repository.Received(2).ListAsync();
    }

    [Fact]
    public async Task IsRevokedAsync_ReloadFailure_Propagates()
    {
        _repository.ListAsync().ThrowsAsync(new InvalidOperationException("postgres is down"));

        var act = () => _sut.IsRevokedAsync("revoked-sub", null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("postgres is down");
    }
}
