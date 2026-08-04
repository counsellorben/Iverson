using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.Client.Core;

/// <summary>
/// Retrieves the tenant's authorized schema catalog for agent consumption. Binds the acting
/// user at construction and applies it internally, so the call surface stays entity- and
/// identity-free.
/// </summary>
public sealed class SchemaCatalogClient(
    ObjectMappingService.ObjectMappingServiceClient mapping,
    Func<Task<string>>? actingUserTokenProvider = null)
{
    public async Task<IReadOnlyList<SchemaType>> GetSchemaAsync(
        string traceId = "", CancellationToken ct = default)
    {
        var headers = new Metadata();
        if (actingUserTokenProvider is not null)
            headers.WithActingUser(await actingUserTokenProvider());

        var response = await mapping.GetSchemaAsync(
            new GetSchemaRequest { TraceId = traceId }, headers, cancellationToken: ct);

        return response.Types_;
    }
}
