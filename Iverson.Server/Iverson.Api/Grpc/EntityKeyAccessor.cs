using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Iverson.Api.Grpc;

public interface IEntityKeyAccessor
{
    string ExtractKey(Struct payload, string keyColumn);
    void SetKey(Struct payload, string keyColumn, string key);

    /// <summary>
    /// Assigns a fresh server-generated UUID v7 key. Clients never assign keys:
    /// a payload that already carries one is rejected, not silently overwritten.
    /// </summary>
    string AssignNewKey(Struct payload, string keyColumn);
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

    public string AssignNewKey(Struct payload, string keyColumn)
    {
        var supplied = ExtractKey(payload, keyColumn);
        if (!string.IsNullOrWhiteSpace(supplied) && supplied != Guid.Empty.ToString())
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"'{keyColumn}' is server-generated and cannot be set by the client. " +
                $"Omit it on create; the assigned key is returned in the response."));

        var key = Guid.CreateVersion7().ToString();
        SetKey(payload, keyColumn, key);
        return key;
    }
}
