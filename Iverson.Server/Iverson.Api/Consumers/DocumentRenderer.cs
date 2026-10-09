using System.Text;
using System.Text.Json;
using Iverson.Api.Schema;

namespace Iverson.Api.Consumers;

/// <summary>
/// Renders a type's parsed <see cref="DocumentTemplate"/> (T1) into plain text by substituting
/// its own scalar properties. Registration validates every placeholder; this type does not
/// re-validate any of it.
///
/// Relation placeholders (<c>{Rel.Prop}</c> and <c>{#Rel}…{/Rel}</c>) are refused at
/// registration, but a descriptor stored before that change still carries the parsed
/// <see cref="DocumentSegmentKind.OneHop"/> and <see cref="DocumentSegmentKind.Block"/> segments,
/// and <c>SchemaRegistry.LoadAsync</c> does not re-validate templates. Both render as nothing —
/// a block's own literal text included — and nothing is fetched for them, so such a template
/// stops copying related data on its next render.
///
/// Rendering is deterministic and culture-invariant (see the plan's Global Constraints): the
/// same row must render byte-identically on any node at any time. Scalar formatting never
/// touches <see cref="System.Globalization.CultureInfo"/> — it reads JSON literals, which are
/// culture-agnostic by construction.
/// </summary>
public sealed class DocumentRenderer
{
    // Task-returning so IntelligenceStoreConsumer's call site is unchanged; nothing here awaits.
    public Task<string> RenderAsync(
        SchemaDescriptor schema, JsonElement payload, string tenantId, CancellationToken ct)
    {
        var template = schema.DocumentTemplate;
        if (template is null) return Task.FromResult(string.Empty);

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
                case DocumentSegmentKind.Block:
                    // Legacy relation segments render as nothing; see the class doc.
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(segment.Kind), segment.Kind,
                        $"Unhandled {nameof(DocumentSegmentKind)} value — add a case above.");
            }
        }

        return Task.FromResult(sb.ToString());
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
    // required formatting without inspecting the declared object/SQL type:
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
}
