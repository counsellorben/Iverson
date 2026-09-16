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
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException(
                "Qdrant:ApiKey is required (used both as the admin API key and the JWT signing secret)",
                nameof(apiKey));
        }
        if (System.Text.Encoding.UTF8.GetByteCount(apiKey) < 32)
        {
            throw new ArgumentException(
                "Qdrant:ApiKey must be at least 32 bytes (used as an HMAC-SHA256 signing key)",
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
        var section = config.GetSection(VectorRankingOptions.Section);
        if (section["Lambda"] is not null)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}:Lambda is no longer read. Set " +
                $"{VectorRankingOptions.Section}:LambdaSimilar and {VectorRankingOptions.Section}:LambdaChunks " +
                "(env VectorRanking__LambdaSimilar / VectorRanking__LambdaChunks) instead.");

        var opts = new VectorRankingOptions();
        section.Bind(opts);

        if (!double.IsFinite(opts.WBase) || !double.IsFinite(opts.WCentroid) ||
            !double.IsFinite(opts.WDecay) || !double.IsFinite(opts.WPopularity) ||
            !double.IsFinite(opts.LambdaSimilar) || !double.IsFinite(opts.LambdaChunks))
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}: every value must be finite " +
                $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, " +
                $"WPopularity={opts.WPopularity}, LambdaSimilar={opts.LambdaSimilar}, " +
                $"LambdaChunks={opts.LambdaChunks}).");

        if (opts.WBase < 0 || opts.WCentroid < 0 || opts.WDecay < 0 || opts.WPopularity < 0)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}: weights must be non-negative " +
                $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, " +
                $"WPopularity={opts.WPopularity}).");

        if (opts.WBase > 1000000 || opts.WCentroid > 1000000 || opts.WDecay > 1000000 || opts.WPopularity > 1000000)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}: weights must be at most 1000000 " +
                $"(WBase={opts.WBase}, WCentroid={opts.WCentroid}, WDecay={opts.WDecay}, " +
                $"WPopularity={opts.WPopularity}). Weights matter only relative to one another, and " +
                "larger values can overflow the fused score to NaN.");

        if (opts.WBase <= 0)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}:WBase must be greater than zero (was {opts.WBase}). " +
                "The base similarity score is the only signal every candidate carries, so a zero WBase " +
                "leaves a candidate whose other present signals all weigh zero with a NaN fused score.");

        if (opts.LambdaSimilar is < 0 or > 1)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}:LambdaSimilar must be in [0,1] (was {opts.LambdaSimilar}).");

        if (opts.LambdaChunks is < 0 or > 1)
            throw new InvalidOperationException(
                $"{VectorRankingOptions.Section}:LambdaChunks must be in [0,1] (was {opts.LambdaChunks}).");

        for (var i = 0; i < opts.SimilarViaChunksTypes.Count; i++)
        {
            var trimmed = opts.SimilarViaChunksTypes[i]?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                throw new InvalidOperationException(
                    $"{VectorRankingOptions.Section}:SimilarViaChunksTypes[{i}] is blank.");
            opts.SimilarViaChunksTypes[i] = trimmed;
        }

        services.AddSingleton(Options.Create(opts));
        services.AddSingleton<IResultReranker, ResultReranker>();
        services.AddSingleton<IResultDiversifier, ResultDiversifier>();
        return services;
    }
}
