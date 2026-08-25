using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // GREEN-WHEN-DELETED, which is exactly what this test exists to stop. The admin console's
    // /admin/console/qdrant endpoint resolves IVectorCollectionReader from DI, and its pipeline
    // tests substitute a fake with RemoveAll<IVectorCollectionReader>() + AddSingleton —
    // RemoveAll on an unregistered service is a NO-OP, so deleting the registration in
    // AddQdrant leaves every Iverson.Api.Tests and Iverson.Vector.Tests case green and fails only
    // as a DI-resolution 500 against a real deployment. Resolving it here from a real built
    // provider is the only assertion in either suite that goes red for that deletion.
    //
    // The reader is resolved, not merely looked up in the descriptor list, so the api-key closure
    // AddQdrant captures has to actually be constructible too.
    [Fact]
    public void AddQdrant_RegistersResolvableVectorCollectionReader()
    {
        var services = new ServiceCollection();
        // The reader takes an ILogger<T>. Supplied directly rather than via AddLogging(), which
        // lives in Microsoft.Extensions.Logging — this project references only the Abstractions.
        services.AddSingleton<ILogger<IntelligenceCollectionReader>>(
            NullLogger<IntelligenceCollectionReader>.Instance);
        services.AddQdrant("localhost", 6334, apiKey: "test-api-key");

        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IVectorCollectionReader>();

        reader.Should().BeOfType<IntelligenceCollectionReader>();
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
