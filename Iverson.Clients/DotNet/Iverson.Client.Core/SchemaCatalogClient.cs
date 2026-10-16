using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.Client.Core;

/// <summary>
/// Retrieves the tenant's authorized schema catalog for agent consumption. Binds the acting
/// user at construction and applies it internally, so the call surface stays entity- and
/// identity-free.
/// </summary>
/// <param name="mapping">The schema-registration client.</param>
/// <param name="identity">
/// The client's acting-user identity; its <see cref="ActingUserIdentity.TokenProvider"/>, if any, is
/// sent on every call. <c>AddIversonClient</c> passes its singleton. An identity that carries a token
/// provider while <see cref="ActingUserIdentity.RefusesPlaintextTokens"/> is true (the default for an
/// identity built by hand) is refused at construction (CSR round-11 #22).
/// </param>
public sealed class SchemaCatalogClient(
    ObjectMappingService.ObjectMappingServiceClient mapping,
    ActingUserIdentity? identity = null)
{
    private readonly Func<Task<string>>? _actingUserTokenProvider =
        ActingUserIdentity.RequireSendableAmbientToken(identity)?.TokenProvider;

    public async Task<IReadOnlyList<SchemaType>> GetSchemaAsync(
        string traceId = "", CancellationToken ct = default)
    {
        var headers = new Metadata();
        if (_actingUserTokenProvider is not null)
            headers.WithActingUser(await _actingUserTokenProvider());

        var response = await mapping.GetSchemaAsync(
            new GetSchemaRequest { TraceId = traceId }, headers, cancellationToken: ct);

        return response.Types_;
    }
}
