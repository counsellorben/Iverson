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

        return ModelIn(json);
    }

    /// <summary>
    /// The model named by one <c>schema_json</c> row, or null when the row carries no embedded
    /// content at all. Extracted from <see cref="FetchModelAsync"/> — which needs a live Postgres
    /// and could therefore be graded by nothing — because this parse is the harness's ONLY window
    /// onto the resolved model: a parse that silently returned null would not FAIL
    /// <c>ModelRejectedScenario.JudgeParity</c>, it would make it vacuous, and null is also the
    /// legitimate answer for a type that carries no embedding.
    ///
    /// <para>vectorFields first, chunkFields second — the same order as SchemaDescriptor.ModelOf,
    /// so a type with only a chunked property is still observable. camelCase because SchemaRegistry
    /// serialises with JsonNamingPolicy.CamelCase.</para>
    ///
    /// <para><b><c>modelId</c> is the right key for BOTH arrays, and it is the one thing here a
    /// reader is likely to "fix" wrongly.</b> The WIRE's <c>PropertyDescriptor</c> has two separate
    /// fields, <c>model_id</c> and <c>chunk_model_id</c> (<c>object_mapping.proto:52,56</c>) — but
    /// this is not the wire. It is the SERVER'S OWN serialized <c>SchemaDescriptor</c>, whose
    /// <c>ChunkDescriptor</c> member is plainly <c>ModelId</c>
    /// (<c>Iverson.Api/Schema/SchemaDescriptor.cs:112-114</c>), so the chunk array's key is
    /// <c>modelId</c> as well. "Correcting" this to <c>chunkModelId</c> for <c>chunkFields</c>
    /// throws on every chunk-only type. <c>SchemaProbeTests</c> pins both arrays against a REAL
    /// serialized descriptor rather than a hand-written literal, so this paragraph is checked
    /// rather than merely believed.</para>
    /// </summary>
    internal static string? ModelIn(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var collection in new[] { "vectorFields", "chunkFields" })
            if (doc.RootElement.TryGetProperty(collection, out var arr) &&
                arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                return arr[0].GetProperty("modelId").GetString();

        return null;
    }
}
