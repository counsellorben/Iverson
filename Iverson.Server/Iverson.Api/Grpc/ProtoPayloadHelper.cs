using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Iverson.Api.Grpc;

internal static class StructSerializer
{
    internal static string SerializePayload(Struct payload) =>
        JsonSerializer.Serialize(
            payload.Fields.ToDictionary(kv => UpperFirst(kv.Key), kv => ProtoValueToObject(kv.Value)));

    internal static string UpperFirst(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static object? ProtoValueToObject(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.StringValue  => v.StringValue,
        Value.KindOneofCase.NumberValue  => v.NumberValue,
        Value.KindOneofCase.BoolValue    => v.BoolValue,
        Value.KindOneofCase.NullValue    => null,
        Value.KindOneofCase.ListValue    => v.ListValue.Values.Select(ProtoValueToObject).ToList(),
        Value.KindOneofCase.StructValue  => v.StructValue.Fields
                                            .ToDictionary(kv => UpperFirst(kv.Key), kv => ProtoValueToObject(kv.Value)),
        _                                => null
    };
}
