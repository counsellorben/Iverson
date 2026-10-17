namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// How long the api waits on a privileged request (CSR round 12 §5.2). RegisterSchema's claim and
/// run waits add to 25 s, below every edge's default timeout on the gRPC path (nginx 60 s, ALB 60 s
/// idle, GCE and AGIC 30 s); a healthy worker answers in about 1 s. A DI singleton, so tests can
/// shorten them.
/// </summary>
internal sealed record PrivilegedRequestWaits
{
    /// <summary>How long RegisterSchema waits for the worker to claim it; also the row's expiry.</summary>
    public TimeSpan RegisterClaim { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How much longer RegisterSchema waits once the worker has claimed it.</summary>
    public TimeSpan RegisterRun { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
