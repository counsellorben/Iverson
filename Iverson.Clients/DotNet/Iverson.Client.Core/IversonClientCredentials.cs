namespace Iverson.Client.Core;

/// <param name="HostHeader">
/// Overrides the HTTP Host header sent to <paramref name="TokenEndpoint"/>, without changing
/// which address is actually connected to. Needed when the token endpoint is reached via a
/// different hostname than the resource server's OIDC Authority expects (e.g. a client running
/// outside a Docker/Kubernetes network hitting a host-exposed port, while the server validates
/// tokens using the in-network hostname) — Authentik's issuer_mode:global stamps the JWT's `iss`
/// claim from the request's Host header, so a mismatch here fails issuer validation with a 401
/// before authorization is ever evaluated. Same problem AuthentikFlowExecutorClient's HostHeader
/// already solves for acting-user tokens.
/// </param>
public sealed record IversonClientCredentials(
    string ClientId,
    string ClientSecret,
    string TokenEndpoint,
    string? Scope = null,
    string? HostHeader = null);
