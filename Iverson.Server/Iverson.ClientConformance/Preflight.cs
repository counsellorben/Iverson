using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Client.Contracts;
using Npgsql;

namespace Iverson.ClientConformance;

/// <summary>
/// Verifies the environment the harness needs is already up before it drives any driver. The
/// harness never starts or stops docker compose itself — every failure here names the down
/// dependency and the command to bring it up.
/// </summary>
public sealed class Preflight(string grpcUrl, string authentikBaseUrl, string postgresCs)
{
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();

        if (await CheckApiAsync(ct) is { } apiFailure)
            failures.Add(apiFailure);

        if (await CheckAuthentikAsync(ct) is { } authentikFailure)
            failures.Add(authentikFailure);

        if (await CheckPostgresAsync(ct) is { } postgresFailure)
            failures.Add(postgresFailure);

        return failures;
    }

    // Proves transport reachability only — an unauthenticated call whose transport succeeds
    // (including one the server rejects with Unauthenticated/PermissionDenied/NotFound) counts as
    // reachable. Only a transport-level failure (the channel can't connect at all) fails preflight.
    private async Task<string?> CheckApiAsync(CancellationToken ct)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(grpcUrl);
            var client = new ObjectMappingService.ObjectMappingServiceClient(channel);
            try
            {
                await client.GetAsync(
                    new MappingGetRequest { TypeName = "__iverson_conformance_preflight_nonexistent_type__", Key = "preflight" },
                    deadline: DateTime.UtcNow.AddSeconds(5),
                    cancellationToken: ct);
            }
            catch (RpcException rpc) when (rpc.StatusCode != StatusCode.Unavailable)
            {
                // Any response from the server — even Unauthenticated or NotFound — proves the
                // transport reached it. Only Unavailable indicates the server itself is down.
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Iverson API is not reachable at {grpcUrl} ({ex.Message}). " +
                   "Bring it up with: docker compose up -d iverson-api";
        }
    }

    private async Task<string?> CheckAuthentikAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(authentikBaseUrl, ct);
            // Any HTTP response (including redirects/4xx/5xx from Authentik itself) proves the
            // transport reached the server; only a connection-level failure fails preflight.
            return null;
        }
        catch (Exception ex)
        {
            return $"Authentik is not reachable at {authentikBaseUrl} ({ex.Message}). " +
                   "Bring it up with: docker compose up -d authentik-server";
        }
    }

    private async Task<string?> CheckPostgresAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(postgresCs);
            await connection.OpenAsync(ct);
            return null;
        }
        catch (Exception ex)
        {
            return $"Postgres is not reachable ({ex.Message}). " +
                   "Bring it up with: docker compose up -d postgres";
        }
    }
}
