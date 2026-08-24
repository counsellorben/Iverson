using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Iverson.Client.Contracts;

namespace Iverson.ClientConformance;

/// <summary>
/// The seam scenarios re-register through. Extracted for the same reason as
/// <see cref="IDriverRunner"/>: a scenario's <c>RunAsync</c> cannot be driven end to end while it
/// depends on a sealed type that opens a live gRPC channel, and an un-driven <c>RunAsync</c> is
/// exactly the ungraded call site Ruling 38 recorded.
/// </summary>
public interface IReregistrar
{
    /// <inheritdoc cref="Reregistrar.ReregisterAsync"/>
    Task ReregisterAsync(
        JsonElement typeDescriptorJson,
        string actingToken,
        string ownerField = "OwnerId",
        CancellationToken ct = default);
}

/// <summary>
/// S1 registers a schema through a driver with no authorization block, then this class
/// re-registers it with one added, so scenarios can exercise the same type both unauthorized and
/// authorized. It takes the driver's reported <see cref="TypeDescriptor"/> verbatim and changes
/// only <see cref="TypeDescriptor.Authorization"/>: <c>SchemaRegistry.RegisterAsync</c> replaces
/// the stored descriptor wholesale (see <c>SchemaRegistry.cs:47-56</c>), so reconstructing the
/// descriptor from scratch here — rather than round-tripping the driver's own JSON — would
/// overwrite the very relation shape S1's depth-1 check exists to inspect.
/// </summary>
public sealed class Reregistrar(ObjectMappingService.ObjectMappingServiceClient client) : IReregistrar
{
    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>
    /// Parses <paramref name="typeDescriptorJson"/> (the register-phase driver's reported
    /// <c>TypeDescriptor</c>, exactly as it appeared in the phase document), sets
    /// <see cref="TypeDescriptor.Authorization"/> to the fixed rules below, and re-registers it.
    /// </summary>
    public async Task ReregisterAsync(
        JsonElement typeDescriptorJson,
        string actingToken,
        string ownerField = "OwnerId",
        CancellationToken ct = default)
    {
        var descriptor = Parser.Parse<TypeDescriptor>(typeDescriptorJson.GetRawText());
        descriptor.Authorization = Rules(ownerField);

        // The acting-user token rides in `x-acting-user-authorization`, NOT in `authorization`:
        // the server reads `authorization` as the SERVICE identity and requires the
        // `schema_admin` scope on it for RegisterSchema (SchemaAdminAuthorizationPolicy.cs),
        // which the acting user does not carry. The service identity is supplied by the channel
        // this client was built on, exactly as the five drivers do it.
        var headers = new Metadata { { "x-acting-user-authorization", $"Bearer {actingToken}" } };
        await client.RegisterSchemaAsync(
            new SchemaRequest
            {
                RootType = descriptor,
                TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty,
            },
            headers,
            cancellationToken: ct);
    }

    private static AuthorizationRules Rules(string ownerField = "OwnerId") => new()
    {
        OwnerField = ownerField,
        RowPermissions =
        {
            new RowPermission { Role = "iverson-loadtest-bypass", CanReadAll = true, CanWriteAll = true, CanDeleteAll = true },
        },
    };
}
