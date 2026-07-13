using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Iverson.Api.Grpc;

internal static class AuthorizationFieldMasking
{
    public static void MaskDisallowedFields(Struct payload, IReadOnlySet<string>? allowedFields)
    {
        if (allowedFields is null) return;

        var toRemove = payload.Fields.Keys
            .Where(key => !allowedFields.Contains(StructSerializer.UpperFirst(key)))
            .ToList();
        foreach (var key in toRemove)
            payload.Fields.Remove(key);
    }

    public static void RejectDisallowedFields(
        Struct payload, IReadOnlySet<string>? allowedFields, string? exemptField = null)
    {
        if (allowedFields is null) return;

        var disallowed = payload.Fields.Keys
            .Select(StructSerializer.UpperFirst)
            .Where(canonical => !allowedFields.Contains(canonical) && canonical != exemptField)
            .ToList();
        if (disallowed.Count > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Field(s) not permitted for this caller: {string.Join(", ", disallowed)}"));
    }
}
