namespace Iverson.StarRocks;

internal sealed class StarRocksReadinessGate(
    Func<CancellationToken, Task<bool>> checkAliveOnceAsync,
    TimeSpan timeout,
    TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
    private readonly object _lock = new();
    private volatile bool _confirmedReady;
    private Task? _pendingWait;

    public Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_confirmedReady) return Task.CompletedTask;

        lock (_lock)
        {
            if (_confirmedReady) return Task.CompletedTask;
            return _pendingWait ??= WaitUntilReadyAsync(ct);
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (await checkAliveOnceAsync(ct).ConfigureAwait(false))
                    {
                        _confirmedReady = true;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }

            throw new StarRocksNotReadyException(
                $"StarRocks backend was not reported alive within {timeout}.", lastError);
        }
        finally
        {
            lock (_lock)
            {
                if (!_confirmedReady) _pendingWait = null;
            }
        }
    }
}
