using Iverson.Sql;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round-10 #11: answers "has this token been revoked?" for every authenticated request
/// (both JwtBearer schemes' <c>OnTokenValidated</c>) from an in-memory snapshot of
/// <see cref="ITokenRevocationRepository"/>, reloaded at most every 30 s — the same staleness
/// <see cref="TenantStatusCache"/> accepts — or at once after <see cref="Invalidate"/>, which
/// also supersedes a reload already in flight: that reload may have read the table before the
/// revocation committed, so it does not publish its snapshot and later callers start afresh. One
/// reload runs at a time; concurrent callers wait for it rather than each querying Postgres, and
/// a caller whose request is cancelled stops waiting without cancelling the reload for the rest.
/// A reload failure propagates to every caller waiting on it, so the request fails rather than
/// skipping the check, as <see cref="TenantStatusCache"/> does; the next call starts a fresh
/// reload. JWT <c>iat</c> has whole-second precision, so a login in the same wall-clock second
/// as a revocation is refused — failing closed — and the user logs in again.
/// </summary>
public sealed class TokenRevocationCache(
    ITokenRevocationRepository repository,
    TimeProvider timeProvider) : ITokenRevocationCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly object _reloadGate = new();
    private volatile Snapshot? _snapshot;
    private Task<Snapshot>? _reload; // guarded by _reloadGate
    private int _generation;         // guarded by _reloadGate; bumped by Invalidate

    private sealed record Snapshot(IReadOnlyDictionary<string, DateTimeOffset> RevokedAt, DateTimeOffset LoadedAt);

    public async Task<bool> IsRevokedAsync(string sub, DateTimeOffset? issuedAt, CancellationToken cancellationToken = default)
    {
        var snapshot = await CurrentSnapshotAsync(cancellationToken);

        // A token with no iat cannot show it was issued after the revocation, so it is refused.
        return snapshot.RevokedAt.TryGetValue(sub, out var revokedAt)
            && (issuedAt is null || issuedAt <= revokedAt);
    }

    public void Invalidate()
    {
        lock (_reloadGate)
        {
            // A reload in flight is abandoned rather than joined: its awaiters still get its
            // result, but it no longer publishes, and the next caller starts a fresh reload.
            _generation++;
            _snapshot = null;
            _reload = null;
        }
    }

    private Task<Snapshot> CurrentSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        if (snapshot is not null && !IsStale(snapshot))
            return Task.FromResult(snapshot);

        Task<Snapshot> reload;
        lock (_reloadGate)
        {
            // Re-check under the lock: a reload that finished since the check above is used
            // instead of starting another.
            snapshot = _snapshot;
            if (snapshot is not null && !IsStale(snapshot))
                return Task.FromResult(snapshot);

            // Join the reload in flight, if any. A completed one is never reused: a success has
            // already published its snapshot (checked above), and a failure must not be replayed
            // to later callers. Task.Run keeps the repository call off this lock even when
            // ListAsync completes synchronously.
            if (_reload is null || _reload.IsCompleted)
            {
                var generation = _generation;
                _reload = Observe(Task.Run(() => ReloadAsync(generation)));
            }
            reload = _reload;
        }

        // The caller's token only ends this caller's wait; the shared reload carries on.
        return reload.WaitAsync(cancellationToken);
    }

    // Every waiter may have cancelled before the reload fails; its failure is then observed here
    // instead of surfacing as an UnobservedTaskException when the task is collected. The original
    // task is returned, so every waiter still sees the same outcome.
    private static Task<Snapshot> Observe(Task<Snapshot> reload)
    {
        reload.ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return reload;
    }

    private async Task<Snapshot> ReloadAsync(int generation)
    {
        var rows = await repository.ListAsync();
        var snapshot = new Snapshot(
            rows.ToDictionary(r => r.Sub, r => r.RevokedAt, StringComparer.Ordinal),
            timeProvider.GetUtcNow());
        lock (_reloadGate)
        {
            // Superseded by Invalidate: the rows may predate the revocation, so only this
            // reload's own awaiters, who arrived before the invalidation, see them.
            if (_generation == generation)
                _snapshot = snapshot;
        }
        return snapshot;
    }

    private bool IsStale(Snapshot snapshot) => timeProvider.GetUtcNow() - snapshot.LoadedAt > Ttl;
}
