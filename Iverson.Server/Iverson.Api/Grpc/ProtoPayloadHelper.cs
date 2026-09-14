using System.Text.Json;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Iverson.Api.Grpc;

internal static class StructSerializer
{
    internal static string SerializePayload(Struct payload) =>
        JsonSerializer.Serialize(FoldKeys(payload.Fields));

    internal static string UpperFirst(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static Dictionary<string, object?> FoldKeys(MapField<string, Value> fields)
    {
        var originalNameOf = new Dictionary<string, string>();
        var folded = new Dictionary<string, object?>();
        foreach (var (key, value) in fields)
        {
            var upper = UpperFirst(key);
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
        Value.KindOneofCase.StructValue  => FoldKeys(v.StructValue.Fields),
        _                                => null
    };
}
