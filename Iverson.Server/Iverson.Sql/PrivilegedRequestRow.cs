namespace Iverson.Sql;

/// <summary>One row of the privileged-request queue (CSR round 12 §5.1).</summary>
/// <param name="Payload">The kind's payload, JSON.</param>
/// <param name="ResultJson">The worker's recorded outcome, JSON; null until the request finishes.</param>
public sealed record PrivilegedRequestRow(Guid Id, string Kind, string Payload, string Status, string? ResultJson);

public static class PrivilegedRequestStatus
{
    public const string Pending   = "pending";
    public const string Running   = "running";
    public const string Succeeded = "succeeded";
    public const string Failed    = "failed";
    public const string Expired   = "expired";
}
