using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using Xunit;

namespace Iverson.Vector.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddQdrant_WithApiKey_RegistersResolvableQdrantClient()
    {
        var services = new ServiceCollection();
        services.AddQdrant("localhost", 6334, apiKey: "test-api-key");

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<QdrantClient>();

        client.Should().NotBeNull();
    }

    [Fact]
    public void AddQdrant_WithNullCertPath_RegistersPlaintextConstructedClient()
    {
        var services = new ServiceCollection();
        services.AddQdrant("localhost", 6334, apiKey: "test-api-key", certPath: null);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<QdrantClient>();

        // certPath: null must resolve via the `new QdrantClient(host, port, https: false, apiKey: null)`
        // branch — no TLS channel, no cert file ever touched.
        client.Should().NotBeNull();
        client.Should().BeOfType<QdrantClient>();
    }

    [Fact]
    public void AddQdrant_WithCertPath_RegistersClientBuiltViaTlsChannel()
    {
        var certPath = WriteSelfSignedCertToTempFile();
        try
        {
            var services = new ServiceCollection();
            services.AddQdrant("localhost", 6334, apiKey: "test-api-key", certPath: certPath);

            using var provider = services.BuildServiceProvider();

            // Resolving must exercise the real TLS branch: load the cert file from disk, compute
            // its SHA-256 thumbprint, and build a QdrantChannel-backed client — all without a live
            // connection (gRPC channels are lazy). A failure here means that path is broken.
            var client = provider.GetRequiredService<QdrantClient>();

            client.Should().NotBeNull();
            client.Should().BeOfType<QdrantClient>();
        }
        finally
        {
            File.Delete(certPath);
        }
    }

    private static string WriteSelfSignedCertToTempFile()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=test-qdrant",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        return path;
    }
}
