using System.Text.RegularExpressions;

namespace Iverson.StarRocks;

public static class TenantIdentifier
{
    private static readonly Regex AllowedPattern = new("^(?!.*--)([A-Za-z0-9_-]{1,52})$", RegexOptions.Compiled);

    public static bool IsValid(string tenantId) => AllowedPattern.IsMatch(tenantId);

    internal static string DatabaseName(string tenantId) => $"iverson_tenant_{tenantId}";

    internal static string RoleName(string tenantId) => $"role_tenant_{tenantId}";

    internal static string Qualify(string? tenantDatabase, string tableName) =>
        tenantDatabase is null ? $"`{tableName}`" : $"`{tenantDatabase}`.`{tableName}`";
}
