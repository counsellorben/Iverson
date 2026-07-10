using Google.Protobuf.WellKnownTypes;

namespace Iverson.Api.Grpc;

public interface IEntityKeyAccessor
{
    string ExtractKey(Struct payload, string keyColumn);
    void SetKey(Struct payload, string keyColumn, string key);
}

public sealed class EntityKeyAccessor : IEntityKeyAccessor
{
    public string ExtractKey(Struct payload, string keyColumn)
    {
        foreach (var candidate in StructFieldAccess.Candidates(keyColumn))
            if (payload.Fields.TryGetValue(candidate, out var v))
                return v.StringValue;
        return string.Empty;
    }

    public void SetKey(Struct payload, string keyColumn, string key)
    {
        foreach (var candidate in StructFieldAccess.Candidates(keyColumn))
            if (payload.Fields.ContainsKey(candidate))
            {
                payload.Fields[candidate] = Value.ForString(key);
                return;
            }
        payload.Fields[keyColumn] = Value.ForString(key);
    }
}
