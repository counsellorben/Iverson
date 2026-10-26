using System.Threading.RateLimiting;

namespace Iverson.Api.RateLimiting;

/// <summary>
/// One caller's partition of a policy that has both a per-caller limit and a ceiling every caller
/// shares (CSR round 12 §2.2, the <c>"traces"</c> relay). A request needs a permit from the
/// caller's own limiter and then one from the shared ceiling, in that order, as
/// <see cref="RateLimiter.CreateChained"/> takes them: a request the caller's own limit refuses
/// spends nothing of the ceiling.
/// <para>
/// The rate-limiting middleware disposes a partition once it has been idle, and a chained limiter
/// disposes none of the limiters it wraps. This one disposes the caller's own limiter with itself,
/// and never the shared ceiling, which every other partition still uses. It is idle when the
/// caller's own limiter is.
/// </para>
/// </summary>
public sealed class SharedCeilingRateLimiter : RateLimiter
{
    private readonly RateLimiter _perCaller;
    private readonly RateLimiter _chain;

    public SharedCeilingRateLimiter(RateLimiter perCaller, RateLimiter sharedCeiling)
    {
        _perCaller = perCaller;
        _chain = CreateChained(perCaller, sharedCeiling);
    }

    public override TimeSpan? IdleDuration => _perCaller.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _chain.GetStatistics();

    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        _chain.AttemptAcquire(permitCount);

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        _chain.AcquireAsync(permitCount, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chain.Dispose();
            _perCaller.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await _chain.DisposeAsync();
        await _perCaller.DisposeAsync();
        await base.DisposeAsyncCore();
    }
}
