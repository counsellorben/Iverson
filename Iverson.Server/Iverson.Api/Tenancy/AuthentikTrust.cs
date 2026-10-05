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
    /// A handler that accepts only a certificate chaining to a CA in the PEM file at
    /// <paramref name="caCertificatePath"/> and matching the requested host. With no path (Development,
    /// the test suites) it is a default handler. The file is read once: after a CA rotation, restart.
    /// </summary>
    public static HttpMessageHandler CreateHandler(string? caCertificatePath)
    {
        if (string.IsNullOrEmpty(caCertificatePath))
            return new SocketsHttpHandler();

        var cas = LoadCas(caCertificatePath);
        return new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, presented, platformChain, errors) => ChainsToCa(cas, presented, platformChain, errors) }
        };
    }

    // Every certificate in the file, so a bundle carries a rotation's old and new CA together.
    private static X509Certificate2Collection LoadCas(string path)
    {
        var cas = new X509Certificate2Collection();
        cas.ImportFromPemFile(path);
        if (cas.Count == 0)
            throw new CryptographicException($"'{path}' contains no PEM certificate.");
        return cas;
    }

    private static bool ChainsToCa(X509Certificate2Collection cas, X509Certificate? presented, X509Chain? platformChain, SslPolicyErrors errors)
    {
        // The platform chain error is expected (the CA is in no system store) and is decided below;
        // a missing certificate or a name mismatch is not.
        if (presented is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(cas);
        // Intermediates the server sent help build the path; trust still ends only at the CA file.
        if (platformChain is not null)
            chain.ChainPolicy.ExtraStore.AddRange(platformChain.ChainPolicy.ExtraStore);
        // A private CA publishes no revocation endpoint.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = new X509Certificate2(presented);
        return chain.Build(leaf);
    }

    /// <summary>
    /// Outside Development the api role refuses to start unless every Authentik hop is https and the
    /// CA is readable. The Helm worker carries no Authentik settings, and the guard never runs for the worker role
    /// (the compose worker mirrors the API's settings).
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
            _ = LoadCas(caPath);
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
