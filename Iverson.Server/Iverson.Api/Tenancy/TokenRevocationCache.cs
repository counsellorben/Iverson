using Iverson.Sql;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round-10 #11: answers "has this token been revoked?" for every authenticated request
/// (both JwtBearer schemes' <c>OnTokenValidated</c>) from an in-memory snapshot of
/// <see cref="ITokenRevocationRepository"/>, reloaded at most every 30 s — the same staleness
/// <see cref="TenantStatusCache"/> accepts. One reload runs at a time; concurrent callers wait
/// for it rather than each querying Postgres. A reload failure propagates, so the request fails
/// rather than skipping the check, as <see cref="TenantStatusCache"/> does.
/// </summary>
public sealed class TokenRevocationCache(
    ITokenRevocationRepository repository,
    TimeProvider timeProvider) : ITokenRevocationCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(IReadOnlyDictionary<string, DateTimeOffset> RevokedAt, DateTimeOffset LoadedAt);

    public async Task<bool> IsRevokedAsync(string sub, DateTimeOffset? issuedAt)
    {
        var snapshot = await CurrentSnapshotAsync();

        // A token with no iat cannot show it was issued after the revocation, so it is refused.
        return snapshot.RevokedAt.TryGetValue(sub, out var revokedAt)
            && (issuedAt is null || issuedAt <= revokedAt);
    }

    private async Task<Snapshot> CurrentSnapshotAsync()
    {
        var snapshot = _snapshot;
        if (snapshot is not null && !IsStale(snapshot))
            return snapshot;

        await _reloadLock.WaitAsync();
        try
        {
            // Re-check under the lock: a caller that waited here behind another caller's reload
            // uses that reload instead of starting its own.
            snapshot = _snapshot;
            if (snapshot is not null && !IsStale(snapshot))
                return snapshot;

            var rows = await repository.ListAsync();
            snapshot = new Snapshot(
                rows.ToDictionary(r => r.Sub, r => r.RevokedAt, StringComparer.Ordinal),
                timeProvider.GetUtcNow());
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private bool IsStale(Snapshot snapshot) => timeProvider.GetUtcNow() - snapshot.LoadedAt > Ttl;
}
