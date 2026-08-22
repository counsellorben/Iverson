using System.Text;
using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.Consumers;

/// <summary>
/// Renders a type's parsed <see cref="DocumentTemplate"/> (T1) into plain text by substituting
/// its own scalar properties and its one-hop relations. Semantic validity of every placeholder
/// — the property/relation exists, a one-hop scalar exists on the target type, <c>{Rel.Prop}</c>
/// is never on a collection relation, <c>{#Rel}</c> is never on a single-valued one — is
/// guaranteed by T2 at registration time; this type does not re-validate any of that.
///
/// Rendering is deterministic and culture-invariant (see the plan's Global Constraints): the
/// same row must render byte-identically on any node at any time. Two things make that true
/// here — scalar formatting never touches <see cref="System.Globalization.CultureInfo"/> (it
/// reads JSON literals, which are culture-agnostic by construction), and block rows are sorted
/// by the target type's key column ascending after every fetch, since neither
/// <see cref="IEntityRepository.FetchManyByKeysAsync"/> nor
/// <see cref="IEntityRepository.FetchByColumnAsync"/> specifies an ORDER BY.
/// </summary>
public sealed class DocumentRenderer(SchemaRegistry registry, IEntityRepository entities)
{
    public async Task<string> RenderAsync(
        SchemaDescriptor schema, JsonElement payload, string tenantId, CancellationToken ct)
    {
        var template = schema.DocumentTemplate;
        if (template is null) return string.Empty;

        // One fetch per distinct relation referenced by the template, regardless of how many
        // placeholders on that relation appear (Global Constraints: batching is required, not
        // optional). OneHop and Block placeholders never share a relation name — T2 rejects a
        // relation used as both single-valued and collection — so the two caches never collide.
        var oneHopRows = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var blockRows  = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);

        foreach (var relationName in template.Segments
                     .Where(s => s.Kind == DocumentSegmentKind.OneHop)
                     .Select(s => s.RelationName!)
                     .Distinct(StringComparer.Ordinal))
        {
            oneHopRows[relationName] = await FetchOneHopAsync(schema, payload, relationName, tenantId, ct);
        }

        foreach (var relationName in template.Segments
                     .Where(s => s.Kind == DocumentSegmentKind.Block)
                     .Select(s => s.RelationName!)
                     .Distinct(StringComparer.Ordinal))
        {
            blockRows[relationName] = await FetchBlockAsync(schema, payload, relationName, tenantId, ct);
        }

        var sb = new StringBuilder();
        foreach (var segment in template.Segments)
        {
            switch (segment.Kind)
            {
                case DocumentSegmentKind.Literal:
                    sb.Append(segment.Text);
                    break;

                case DocumentSegmentKind.Scalar:
                    sb.Append(FormatProperty(payload, segment.PropertyName!));
                    break;

                case DocumentSegmentKind.OneHop:
                    var row = oneHopRows[segment.RelationName!];
                    if (row is not null)
                        sb.Append(FormatProperty(row.Value, segment.PropertyName!));
                    break;

                case DocumentSegmentKind.Block:
                    // An empty collection contributes nothing to the rendered text — not even
                    // the block's own literal segments — because the loop below simply never
                    // executes when blockRows[...] is empty.
                    foreach (var relatedRow in blockRows[segment.RelationName!])
                    foreach (var inner in segment.Inner!)
                    {
                        sb.Append(inner.Kind == DocumentSegmentKind.Literal
                            ? inner.Text
                            : FormatProperty(relatedRow, inner.PropertyName!));
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(segment.Kind), segment.Kind,
                        $"Unhandled {nameof(DocumentSegmentKind)} value — add a case above.");
            }
        }

        return sb.ToString();
    }

    // {Rel.Prop} is only valid (T2) on a single-valued relation (OneToOne/ManyToOne), whose
    // foreign key is a single id living on THIS row. Fetched via FetchManyByKeysAsync with a
    // one-element key list rather than FetchByKeyAsync so a relation referenced by several
    // OneHop placeholders still costs one call — the batching contract is per relation, not
    // per placeholder-kind.
    private async Task<JsonElement?> FetchOneHopAsync(
        SchemaDescriptor schema, JsonElement payload, string relationName, string tenantId, CancellationToken ct)
    {
        var relation = schema.Relations.FirstOrDefault(
            r => string.Equals(r.PropertyName, relationName, StringComparison.OrdinalIgnoreCase));
        if (relation is null) return null;
        var targetSchema = registry.Get(relation.RelatedTypeName);
        if (targetSchema is null) return null;

        var fkValue = ExtractString(payload, relation.ForeignKey);
        if (fkValue is null) return null;

        var rows = await entities.FetchManyByKeysAsync(
            SchemaBuilder.ToTableSchema(targetSchema), [fkValue], tenantScoped: true, tenantId: tenantId);
        var match = rows.FirstOrDefault();
        return match is null ? null : JsonDocument.Parse(match.Data).RootElement.Clone();
    }

    // {#Rel} is only valid (T2) on a collection relation (OneToMany/ManyToMany). ManyToMany's
    // foreign key is an id array living on THIS row, so it batches through
    // FetchManyByKeysAsync exactly like a OneHop relation. OneToMany's foreign key instead
    // lives on the RELATED row pointing back at this one, so it is a single
    // FetchByColumnAsync keyed on this row's own key.
    //
    // Rows are sorted by the target type's key column ascending after the fetch — neither
    // repository call specifies an ORDER BY (Iverson.Sql/EntityRepository.cs), so without this
    // sort the same entity could render differently on different nodes depending on the order
    // Postgres happens to return rows in, producing different chunks and different embeddings
    // for byte-identical source data. UUID keys are unique, so this is a total order.
    private async Task<List<JsonElement>> FetchBlockAsync(
        SchemaDescriptor schema, JsonElement payload, string relationName, string tenantId, CancellationToken ct)
    {
        var relation = schema.Relations.FirstOrDefault(
            r => string.Equals(r.PropertyName, relationName, StringComparison.OrdinalIgnoreCase));
        if (relation is null) return [];
        var targetSchema = registry.Get(relation.RelatedTypeName);
        if (targetSchema is null) return [];

        var targetTable = SchemaBuilder.ToTableSchema(targetSchema);

        if (relation.Kind == RelationKind.ManyToMany)
        {
            var keys = ExtractKeys(payload, relation.ForeignKey);
            if (keys.Count == 0) return [];

            var rows = await entities.FetchManyByKeysAsync(
                targetTable, keys, tenantScoped: true, tenantId: tenantId);
            return rows
                .OrderBy(r => r.Key, StringComparer.Ordinal)
                .Select(r => JsonDocument.Parse(r.Data).RootElement.Clone())
                .ToList();
        }

        // OneToMany.
        var ownKey = ExtractString(payload, schema.KeyColumn.Name);
        if (ownKey is null) return [];

        var childRows = await entities.FetchByColumnAsync(
            targetTable, relation.ForeignKey, ownKey, tenantScoped: true, tenantId: tenantId);
        return childRows
            .Select(json => JsonDocument.Parse(json).RootElement.Clone())
            .OrderBy(el => ExtractString(el, targetSchema.KeyColumn.Name), StringComparer.Ordinal)
            .ToList();
    }

    private static string FormatProperty(JsonElement context, string propertyName)
    {
        var value = ExtractElement(context, propertyName);
        return value is null ? string.Empty : FormatScalar(value.Value);
    }

    // Fixed per type, culture-invariant (Global Constraints). Every value the renderer ever
    // reads arrives as a JsonElement produced by System.Text.Json — which serializes Guid as
    // lowercase-D, DateTime/DateTimeOffset as ISO 8601, and numerics without group separators,
    // all independent of thread culture — so JsonValueKind alone is enough to reproduce the
    // required formatting without inspecting the declared CLR/SQL type:
    //   string        -> verbatim
    //   number/bool   -> the raw JSON literal (already invariant, no group separators)
    //   array         -> elements formatted the same way, joined with ", "
    //   null/missing  -> empty string
    private static string FormatScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array  => string.Join(", ", value.EnumerateArray().Select(FormatScalar)),
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => value.GetRawText(),
        _ => string.Empty
    };

    private static JsonElement? ExtractElement(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var v)) return v;

        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (payload.TryGetProperty(camel, out var vc)) return vc;

        foreach (var prop in payload.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return prop.Value;
        }

        return null;
    }

    private static List<string> ExtractKeys(JsonElement payload, string propertyName)
    {
        var element = ExtractElement(payload, propertyName);
        if (element is not { ValueKind: JsonValueKind.Array } array) return [];

        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    // A deliberate third copy of the pattern in EnrichmentConsumer.cs:330 and
    // IntelligenceStoreConsumer.cs:655 — the spec authorizes adding this copy here rather than
    // extracting a shared helper and touching either of those files.
    private static string? ExtractString(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString()
                 : v.ValueKind == JsonValueKind.Null   ? null
                 : v.ToString();

        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (payload.TryGetProperty(camel, out var vc))
            return vc.ValueKind == JsonValueKind.String ? vc.GetString()
                 : vc.ValueKind == JsonValueKind.Null   ? null
                 : vc.ToString();

        return null;
    }
}
