using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        services.AddSingleton<IntelligenceVectorService>();
        services.AddSingleton<IVectorQueryService>(sp => sp.GetRequiredService<IntelligenceVectorService>());
        services.AddSingleton<IVectorWriteService>(sp => sp.GetRequiredService<IntelligenceVectorService>());

        services.AddSingleton(sp => new IntelligenceCollectionManager(
            sp.GetRequiredService<QdrantClient>(), apiKey, sp.GetRequiredService<ILogger<IntelligenceCollectionManager>>()));
        services.AddSingleton<IVectorSchemaManager>(sp => sp.GetRequiredService<IntelligenceCollectionManager>());

        services.AddSingleton(new IntelligenceTenantScope(apiKey));

        return services;
    }

    public static IServiceCollection AddVectorRanking(this IServiceCollection services, IConfiguration config)
    {
        var opts = new VectorRankingOptions();
        config.GetSection(VectorRankingOptions.Section).Bind(opts);

        if (!double.IsFinite(opts.WBase) || !double.IsFinite(opts.WCentroid) ||
            !double.IsFinite(opts.WDecay) || !double.IsFinite(opts.Lambda))
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}: every value must be finite " +
                $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, Lambda={opts.Lambda}).");

        if (opts.WBase < 0 || opts.WCentroid < 0 || opts.WDecay < 0)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}: weights must be non-negative " +
                $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}).");

        if (opts.WBase + opts.WCentroid + opts.WDecay <= 0)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}: at least one weight must be greater than zero; " +
                "all-zero weights make every fused score NaN.");

        if (opts.Lambda is < 0 or > 1)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}:Lambda must be in [0,1] (was {opts.Lambda}).");

        services.AddSingleton(Options.Create(opts));
        services.AddSingleton<IResultReranker, ResultReranker>();
        services.AddSingleton<IResultDiversifier, ResultDiversifier>();
        return services;
    }
}
