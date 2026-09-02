using System.Text.Json;
using Npgsql;

namespace Iverson.ClientConformance;

/// <summary>
/// Reads a registered type's resolved embedding model straight out of Postgres. The harness
/// cannot deserialize SchemaDescriptor — Iverson.ClientConformance has no project reference to
/// Iverson.Api — and the model is not on the wire, so this is the only way to observe it.
///
/// The table name is this project's OWN copy, for exactly the reason PostgresProbe.TableName is
/// (PostgresProbe.cs:20-23): a harness sharing the server's own constant could not catch the
/// server using a different one. Connects as the table-owning role, like PostgresProbe.
/// </summary>
public sealed class SchemaProbe(string connectionString)
{
    public const string SchemaTable = "_iverson_schema";

    /// <summary>The resolved model on <paramref name="typeName"/>'s registered schema, or null
    /// when the type is unregistered or carries no embedded content.</summary>
    public async Task<string?> FetchModelAsync(string typeName, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            $"SELECT schema_json FROM {SchemaTable} WHERE type_name = @t", connection);
        command.Parameters.AddWithValue("t", typeName);

        if (await command.ExecuteScalarAsync(ct) is not string json) return null;

        // vectorFields first, chunkFields second — the same order as SchemaDescriptor.ModelOf, so
        // a type with only a chunked property is still observable. camelCase because
        // SchemaRegistry serialises with JsonNamingPolicy.CamelCase.
        using var doc = JsonDocument.Parse(json);
        foreach (var collection in new[] { "vectorFields", "chunkFields" })
            if (doc.RootElement.TryGetProperty(collection, out var arr) &&
                arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                return arr[0].GetProperty("modelId").GetString();

        return null;
    }
}
