using Iverson.Sql;

namespace Iverson.Api.Tenancy;

internal static class TenantSchema
{
    public const string TableName = "IversonTenants";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("Id", "text", false),
        new List<ColumnSchema>
        {
            new("DisplayName", "text", false),
            new("Status",      "text", false),
            new("CreatedAt",   "timestamptz", false),
        });
}
