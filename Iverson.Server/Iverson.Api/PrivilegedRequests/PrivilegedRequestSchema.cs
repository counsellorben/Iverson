using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// The privileged-request queue (CSR round 12 §5.1), created at startup through ApplySchemaAsync
/// like the other plumbing tables. It has no tenant column, so it has no RLS policy.
/// </summary>
internal static class PrivilegedRequestSchema
{
    public const string TableName = "IversonPrivilegedRequests";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("Id", "uuid", false),
        new List<ColumnSchema>
        {
            new("Kind",        "text", false),
            new("Payload",     "jsonb", false),
            new("RequestedBy", "text", true),
            new("Status",      "text", false),
            new("ResultJson",  "jsonb", true),
            new("RequestedAt", "timestamptz", false),
            new("ExpiresAt",   "timestamptz", true),
            new("CompletedAt", "timestamptz", true),
            new("ClaimedAt",   "timestamptz", true),
        });
}
