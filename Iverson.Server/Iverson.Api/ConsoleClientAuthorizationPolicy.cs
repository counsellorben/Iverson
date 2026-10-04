namespace Iverson.Api;

// CSR round-10 #13: /v1/traces accepts only tokens the admin console's own OIDC client issued,
// told apart by their aud (Authentication:ConsoleAudience). Fails closed: with no console
// audience configured, no caller satisfies it.
public static class ConsoleClientAuthorizationPolicy
{
    public static bool IsSatisfiedBy(IEnumerable<string> audienceClaims, string? consoleAudience) =>
        !string.IsNullOrEmpty(consoleAudience) &&
        audienceClaims.Any(audience => string.Equals(audience, consoleAudience, StringComparison.Ordinal));
}
