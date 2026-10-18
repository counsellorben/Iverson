using System.Diagnostics;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>The api's side of a privileged request (CSR round 12 §5.2): queue it, then poll it.</summary>
internal sealed class PrivilegedRequestClient(IPrivilegedRequestRepository requests, PrivilegedRequestWaits waits)
{
    public PrivilegedRequestWaits Waits => waits;

    public async Task<Guid> SubmitAsync(string kind, string payloadJson, string? requestedBy, TimeSpan? expiresAfter)
    {
        var id = Guid.CreateVersion7();
        await requests.InsertAsync(id, kind, payloadJson, requestedBy, expiresAfter);
        return id;
    }

    public async Task<PrivilegedRequestRow> ReadAsync(Guid id) =>
        await requests.GetAsync(id)
        ?? throw new InvalidOperationException($"Privileged request {id} is no longer recorded.");

    /// <summary>
    /// Re-reads the request every <see cref="PrivilegedRequestWaits.PollInterval"/> while its status
    /// satisfies <paramref name="keepWaiting"/>, for at most <paramref name="timeout"/>, and returns
    /// the last row read. What a status that still satisfies it means is the caller's to decide.
    /// </summary>
    public async Task<PrivilegedRequestRow> WaitWhileAsync(
        Guid id, Func<string, bool> keepWaiting, TimeSpan timeout, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var row = await ReadAsync(id);
            if (!keepWaiting(row.Status) || Stopwatch.GetElapsedTime(started) >= timeout)
                return row;
            await Task.Delay(waits.PollInterval, ct);
        }
    }

    public Task<bool> ExpireIfPendingAsync(Guid id) => requests.ExpireIfPendingAsync(id);
}
