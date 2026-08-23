using System.Text;
using Npgsql;

namespace Iverson.ClientConformance;

/// <summary>
/// Reads a row straight out of Postgres, bypassing the server entirely — the third, independent
/// leg of S1's three-way comparison.
///
/// Two things make this need no configuration beyond the connection string. The table name is
/// derived exactly as the server derives it (<c>Iverson.Server/Iverson.Api/Schema/SchemaBuilder.cs:30</c>:
/// <c>ToSnakeCase(TypeName) + "s"</c>), and the harness connects as the role that owns those
/// tables, which is not subject to their row-level security policies — they are created with
/// ENABLE, not FORCE, ROW LEVEL SECURITY (<c>Iverson.Sql/PostgresSchemaManager.cs:138-148</c>), so
/// the probe sees the row without setting <c>app.tenant_id</c>.
/// </summary>
public sealed class PostgresProbe(string connectionString)
{
    /// <summary>
    /// The server-owned tenant column, as this project's OWN copy of
    /// <c>Iverson.Api.Schema.SchemaDescriptor.TenantColumnName</c>. Kept separate for exactly the
    /// reason <see cref="TableName"/> is: <c>Iverson.ClientConformance</c> has no project reference
    /// to <c>Iverson.Api</c>, and a harness sharing the server's own constant could not catch the
    /// server using a different name from the one the standard publishes.
    ///
    /// <para>CROSS-TASK CONTRACT: if the server's reserved name changes, this copy must change with
    /// it or every assertion that reads the column silently starts reading nothing. It is the
    /// single copy for the whole harness — <c>IdentityScenario</c> reads the column through it and
    /// <c>TenantRejectedScenario</c> builds its rejection fixtures from it.</para>
    /// </summary>
    public const string ServerOwnedTenantColumn = "__TenantId";

    /// <summary>
    /// Mirrors <c>SchemaBuilder.BuildDescriptor</c>'s table naming. Kept as a separate copy on
    /// purpose: <c>NamingExtensions</c> is internal to Iverson.Api, and a probe that shared the
    /// server's helper could not catch the server naming a table differently from what it
    /// documents.
    /// </summary>
    public static string TableName(string typeName)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < typeName.Length; i++)
        {
            if (char.IsUpper(typeName[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(typeName[i]));
        }
        return sb.Append('s').ToString();
    }

    /// <summary>
    /// The row for <paramref name="key"/>, column name to value, or null when no such row exists.
    /// Column names come back exactly as the server created them (quoted property names).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>?> FetchRowAsync(
        string typeName, string keyColumn, string key, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // The table and key column are derived from the registered schema, never from user
        // input, but they are still quoted-and-escaped rather than interpolated raw so that a
        // pathological type name can only ever produce a "relation does not exist" error.
        var sql = $"""SELECT * FROM {Quote(TableName(typeName))} WHERE {Quote(keyColumn)} = @key""";

        await using var command = new NpgsqlCommand(sql, connection);
        // The key column is uuid for every S1 type; Npgsql needs a Guid, not a string, or the
        // primary-key index is skipped and the comparison fails outright.
        command.Parameters.AddWithValue("key", Guid.Parse(key));

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);

        return row;
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
