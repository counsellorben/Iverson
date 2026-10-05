using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round-10 #5: the API reaches Authentik (OIDC discovery, JWKS, the admin API) over TLS on the
/// tls-proxy sidecar's port 8443, trusting only the CA the deployment mounts at
/// <c>Authentik:CaCertificatePath</c>.
/// </summary>
internal static class AuthentikTrust
{
    /// <summary>
    /// A handler that accepts only a certificate chaining to the CA at <paramref name="caCertificatePath"/>
    /// and matching the requested host. With no path (Development, the test suites) it is a default handler.
    /// </summary>
    public static HttpMessageHandler CreateHandler(string? caCertificatePath)
    {
        if (string.IsNullOrEmpty(caCertificatePath))
            return new SocketsHttpHandler();

        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(caCertificatePath));
        return new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, errors) => ChainsToCa(ca, presented, errors) }
        };
    }

    private static bool ChainsToCa(X509Certificate2 ca, X509Certificate? presented, SslPolicyErrors errors)
    {
        // The platform chain error is expected (the CA is in no system store) and is decided below;
        // a missing certificate or a name mismatch is not.
        if (presented is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        // A private CA publishes no revocation endpoint.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(presented));
    }

    /// <summary>
    /// Outside Development the api role refuses to start unless every Authentik hop is https and the
    /// CA is readable. The worker role never calls Authentik and carries no Authentik settings.
    /// </summary>
    public static void ValidateStartup(IConfiguration cfg, string workloadRole, IHostEnvironment env)
    {
        if (workloadRole != "api" || env.IsDevelopment())
            return;

        RequireHttps(cfg, "Authentication:MetadataAddress");
        RequireHttps(cfg, "Authentication:ActingUser:MetadataAddress");
        RequireHttps(cfg, "Authentik:BaseUrl");
        if (string.IsNullOrWhiteSpace(cfg["Authentication:InternalIssuer"]))
            throw new InvalidOperationException("Authentication:InternalIssuer is required outside Development.");

        var caPath = cfg["Authentik:CaCertificatePath"];
        if (string.IsNullOrWhiteSpace(caPath))
            throw new InvalidOperationException("Authentik:CaCertificatePath is required outside Development.");
        try
        {
            _ = X509Certificate2.CreateFromPem(File.ReadAllText(caPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"Authentik:CaCertificatePath '{caPath}' is not a readable PEM certificate.", ex);
        }
    }

    private static void RequireHttps(IConfiguration cfg, string key)
    {
        var value = cfg[key];
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{key} must be an https:// URL outside Development, got '{value}'.");
    }
}
