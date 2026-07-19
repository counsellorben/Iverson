using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Iverson.Vector;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQdrant(
        this IServiceCollection services,
        string host,
        int port = 6334,
        string? apiKey = null,
        string? certPath = null)
    {
        if (apiKey is null)
        {
            throw new ArgumentException(
                "Qdrant:ApiKey is required (used both as the admin API key and the JWT signing secret)",
                nameof(apiKey));
        }

        services.AddSingleton(_ =>
        {
            if (certPath is not null)
            {
                using var cert = X509CertificateLoader.LoadCertificateFromFile(certPath);
                var thumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
                var channel = QdrantChannel.ForAddress($"https://{host}:{port}", new ClientConfiguration
                {
                    CertificateThumbprint = thumbprint
                });
                return new QdrantClient(new QdrantGrpcClient(channel));
            }
            return new QdrantClient(host, port, https: false, apiKey: null);
        });
        services.AddSingleton<QdrantVectorService>();
        services.AddSingleton<IVectorQueryService>(sp => sp.GetRequiredService<QdrantVectorService>());
        services.AddSingleton<IVectorWriteService>(sp => sp.GetRequiredService<QdrantVectorService>());

        services.AddSingleton(sp => new QdrantCollectionManager(
            sp.GetRequiredService<QdrantClient>(), apiKey, sp.GetRequiredService<ILogger<QdrantCollectionManager>>()));
        services.AddSingleton<IVectorSchemaManager>(sp => sp.GetRequiredService<QdrantCollectionManager>());

        services.AddSingleton(new QdrantTenantScope(apiKey));

        return services;
    }
}
