using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Consumers;

/// <summary>
/// The one tenant-resolution rule every entity-event projection consumer applies — the
/// projection side of CSR #7. Created/Updated re-derive from the authoritative Postgres row,
/// because the event payload is unsigned; Deleted reads the pre-delete snapshot, because the row
/// is already gone. A gone row returns <c>null</c> (the caller drops: the entity's Deleted event
/// follows). A row or snapshot that is present but carries no tenant value, or does not parse,
/// is an invariant violation — every write path stamps the tenant — and dead-letters.
/// </summary>
internal static class ProjectionTenantResolution
{
    internal static async Task<AuthoritativeRow?> FetchAuthoritativeRowAsync(
        IEntityRepository entities, SchemaDescriptor schema, string key, string consumer)
    {
        // Cross-tenant by necessity: this read is what derives the tenant, so there is no tenant
        // to scope it to yet, and the unsigned event payload must not be trusted for one.
        var rowJson = await entities.FetchByKeyAsync(
            SchemaBuilder.ToTableSchema(schema), key, EntityAccess.CrossTenantMaintenance);
        if (rowJson is null) return null;

        JsonElement row;
        try
        {
            using var doc = JsonDocument.Parse(rowJson);
            row = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException(
                $"{consumer} Malformed authoritative row JSON type={schema.TypeName} key={key}", ex);
        }

        var tenant = ReadString(row, schema.TenantColumn)
            ?? throw new PoisonMessageException(
                $"{consumer} No tenant value on authoritative row type={schema.TypeName} key={key}");

        return new AuthoritativeRow(tenant, row);
    }

    internal static string TenantFromSnapshot(
        string payloadJson, SchemaDescriptor schema, string key, string consumer)
    {
        JsonElement snapshot;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            snapshot = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException(
                $"{consumer} Malformed delete snapshot JSON type={schema.TypeName} key={key}", ex);
        }

        return ReadString(snapshot, schema.TenantColumn)
            ?? throw new PoisonMessageException(
                $"{consumer} No tenant value in delete snapshot type={schema.TypeName} key={key}");
    }

    // Exact name, then camelCase fallback. JSON null is absent (null), never "" — the
    // JsonElement.ToString() of a Null is "", which is how two of the replaced copies let a
    // null-tenant snapshot through.
    internal static string? ReadString(JsonElement element, string propertyName)
    {
        static string? Extract(JsonElement e) =>
            e.ValueKind == JsonValueKind.String ? e.GetString()
            : e.ValueKind == JsonValueKind.Null ? null
            : e.ToString();

        if (element.TryGetProperty(propertyName, out var v))
            return Extract(v);

        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(camel, out var vc))
            return Extract(vc);

        return null;
    }
}

internal sealed record AuthoritativeRow(string TenantId, JsonElement Row)
{
    internal string? ReadString(string propertyName) => ProjectionTenantResolution.ReadString(Row, propertyName);
}
