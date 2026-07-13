using Grpc.Core;

namespace Iverson.Client.Core;

public static class ActingUserMetadata
{
    public const string MetadataKey = "x-acting-user-authorization";

    public static Metadata WithActingUser(this Metadata headers, string token)
    {
        headers.Add(MetadataKey, $"Bearer {token}");
        return headers;
    }
}
