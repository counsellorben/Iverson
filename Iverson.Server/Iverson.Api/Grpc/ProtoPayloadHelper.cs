using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

internal static class StructSerializer
{
    // A nested object's keys never name a column.
    private static readonly HashSet<string> s_noColumns = [];

    // Keyed on the descriptor instance, as DecayFieldResolver's cache is: SchemaRegistry replaces a
    // type's instance wholesale when it reloads or re-registers it. Only read after it is built.
    private static readonly ConditionalWeakTable<SchemaDescriptor, HashSet<string>> s_columnNames = new();

    /// <summary>
    /// Serializes a write payload. A top-level key that is exactly one of <paramref name="schema"/>'s
    /// column names is written as it is. Every other key, nested keys included, is upper-firsted.
    /// The write RPCs pass the schema after <see cref="PayloadKeyCanonicalizer"/> has given every
    /// column key its registered spelling. So a column whose name starts lower-case (<c>url</c>)
    /// reaches Postgres' case-sensitive <c>json_populate_record</c> under its own name, not as
    /// <c>Url</c> (CSR round 12 §2.3).
    /// </summary>
    internal static string SerializePayload(Struct payload, SchemaDescriptor? schema = null) =>
        JsonSerializer.Serialize(FoldKeys(payload.Fields, schema is null
            ? s_noColumns
            : s_columnNames.GetValue(schema, s =>
                s.ScalarColumns.Select(c => c.Name).Append(s.KeyColumn.Name).ToHashSet(StringComparer.Ordinal))));

    internal static string UpperFirst(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static Dictionary<string, object?> FoldKeys(MapField<string, Value> fields, IReadOnlySet<string> columnNames)
    {
        var originalNameOf = new Dictionary<string, string>();
        var folded = new Dictionary<string, object?>();
        foreach (var (key, value) in fields)
        {
            var upper = columnNames.Contains(key) ? key : UpperFirst(key);
            if (originalNameOf.TryGetValue(upper, out var collidingOriginal))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Payload fields '{collidingOriginal}' and '{key}' both fold to '{upper}' after " +
                    "case-normalization; rename one of them."));
            }
            originalNameOf[upper] = key;
            folded[upper] = ProtoValueToObject(value);
        }
        return folded;
    }

    private static object? ProtoValueToObject(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.StringValue  => v.StringValue,
        Value.KindOneofCase.NumberValue  => v.NumberValue,
        Value.KindOneofCase.BoolValue    => v.BoolValue,
        Value.KindOneofCase.NullValue    => null,
        Value.KindOneofCase.ListValue    => v.ListValue.Values.Select(ProtoValueToObject).ToList(),
        Value.KindOneofCase.StructValue  => FoldKeys(v.StructValue.Fields, s_noColumns),
        _                                => null
    };
}
