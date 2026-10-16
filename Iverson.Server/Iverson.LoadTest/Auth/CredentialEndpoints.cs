namespace Iverson.LoadTest.Auth;

/// <summary>
/// CSR round-11 #15: where LoadTest and the client conformance harness may send credentials. Both
/// send an Operator-level service token and Authentik user passwords to three endpoints: the gRPC
/// URL, the token endpoint and the Authentik flow base URL. A loopback address never leaves the
/// machine; any other address needs TLS. Some of their credential-bearing calls bypass the SDK's
/// transport check (LoadTest's provisioning channel and raw acting-user metadata, the harness's own
/// channel), so this startup check is what keeps credentials off a plaintext network hop. It sees
/// only the configured URLs: a token endpoint that redirects is followed.
/// </summary>
public static class CredentialEndpoints
{
    public const string TokenEndpointPath = "/application/o/token/";

    /// <summary>
    /// Why these endpoints may not carry credentials, or null when they may. The token endpoint's
    /// shape is checked first, then the gRPC URL (loopback, or https when
    /// <paramref name="grpcMayUseTls"/>), then the token endpoint and the Authentik base URL
    /// (https or loopback), and last, with a non-loopback https gRPC URL, an https token endpoint.
    /// </summary>
    public static string? Check(string grpcUrl, string? tokenEndpoint, bool grpcMayUseTls)
    {
        if (tokenEndpoint is not null
            && !(Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out _)
                 && tokenEndpoint.EndsWith(TokenEndpointPath, StringComparison.Ordinal)))
            return $"IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in {TokenEndpointPath} " +
                   "(an Authentik token endpoint).";

        if (!Uri.TryCreate(grpcUrl, UriKind.Absolute, out var grpc)
            || !(grpc.IsLoopback || (grpcMayUseTls && grpc.Scheme == Uri.UriSchemeHttps)))
            return grpcMayUseTls
                ? "IVERSON_GRPC_URL must be https:// or a loopback address: the service and acting-user tokens are sent on this channel."
                : "IVERSON_GRPC_URL must be a loopback address: this harness's channel is plaintext and carries the service and acting-user tokens.";

        if (tokenEndpoint is not null && !IsHttpsOrLoopback(tokenEndpoint))
            return "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address: the client secret or assertion is sent to it.";

        // CIR-1 §3.1 C (user decision 2026-10-12): with a non-loopback https:// gRPC URL the client
        // library is not opted in to plaintext credentials, so it refuses a plaintext token endpoint
        // when the client is registered; refuse that pairing here, before any credential is sent.
        if (grpcMayUseTls && !grpc.IsLoopback && tokenEndpoint is not null
            && !(Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var token) && token.Scheme == Uri.UriSchemeHttps))
            return "IVERSON_TOKEN_ENDPOINT must be https:// when IVERSON_GRPC_URL is not a loopback address: " +
                   "the client library sends client credentials only to an https token endpoint on such a channel.";

        if (!IsHttpsOrLoopback(AuthentikBaseUrl(tokenEndpoint)))
            return "The Authentik base URL (IVERSON_TOKEN_ENDPOINT without its path) must be https:// or a " +
                   "loopback address: user passwords and TOTP codes are sent to it.";

        return null;
    }

    /// <summary>
    /// The Authentik flow base URL: the token endpoint up to its <see cref="TokenEndpointPath"/>,
    /// or the compose default when none is set. Call it only after <see cref="Check"/> accepted the
    /// token endpoint.
    /// </summary>
    public static string AuthentikBaseUrl(string? tokenEndpoint) => tokenEndpoint is not null
        ? tokenEndpoint[..tokenEndpoint.IndexOf(TokenEndpointPath, StringComparison.Ordinal)]
        : "http://localhost:9000";

    public static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static bool IsHttpsOrLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
}
