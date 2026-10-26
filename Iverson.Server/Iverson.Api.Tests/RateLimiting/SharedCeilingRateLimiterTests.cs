using System.Threading.RateLimiting;
using FluentAssertions;
using Iverson.Api.RateLimiting;
using Xunit;

namespace Iverson.Api.Tests.RateLimiting;

/// <summary>
/// CSR round 12 §2.2: a <c>"traces"</c> partition takes the caller's own permit, then one from the
/// ceiling every caller shares, and its disposal never reaches the shared ceiling.
/// </summary>
public class SharedCeilingRateLimiterTests
{
    private static SlidingWindowRateLimiter Window(int permits) => new(new SlidingWindowRateLimiterOptions
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        SegmentsPerWindow = 6,
        QueueLimit = 0
    });

    private static bool Acquired(RateLimiter limiter)
    {
        using var lease = limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    [Fact]
    public void ARequest_NeedsAPermitFromTheCallersOwnLimiter_AndFromTheSharedCeiling()
    {
        using var ceiling = Window(3);
        using var alice = new SharedCeilingRateLimiter(Window(2), ceiling);
        using var bob = new SharedCeilingRateLimiter(Window(2), ceiling);

        Acquired(alice).Should().BeTrue();
        Acquired(alice).Should().BeTrue();
        Acquired(alice).Should().BeFalse("alice's own limit is 2, and a refusal there spends none of the ceiling");
        Acquired(bob).Should().BeTrue("the ceiling of 3 has one permit left");
        Acquired(bob).Should().BeFalse("the shared ceiling is spent, although bob's own limit is not");
    }

    [Fact]
    public void Dispose_DisposesTheCallersOwnLimiter_ButNotTheSharedCeiling()
    {
        using var ceiling = Window(3);
        var perCaller = Window(2);
        var partition = new SharedCeilingRateLimiter(perCaller, ceiling);

        partition.Dispose();

        perCaller.Invoking(l => l.AttemptAcquire()).Should().Throw<ObjectDisposedException>();
        Acquired(ceiling).Should().BeTrue();
    }

    // The middleware disposes an idle partition through DisposeAsync.
    [Fact]
    public async Task DisposeAsync_DisposesTheCallersOwnLimiter_ButNotTheSharedCeiling()
    {
        using var ceiling = Window(3);
        var perCaller = Window(2);
        var partition = new SharedCeilingRateLimiter(perCaller, ceiling);

        await partition.DisposeAsync();

        perCaller.Invoking(l => l.AttemptAcquire()).Should().Throw<ObjectDisposedException>();
        Acquired(ceiling).Should().BeTrue();
    }
}
