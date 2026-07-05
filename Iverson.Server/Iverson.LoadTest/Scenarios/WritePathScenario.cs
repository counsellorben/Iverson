using Iverson.Client.Core;
using Iverson.LoadTest.Entities;
using Microsoft.Extensions.Logging;

namespace Iverson.LoadTest.Scenarios;

/// <summary>
/// write-path benchmark against a plaintext, unauthenticated Kafka broker (docker-compose target).
/// See <see cref="KindWritePathScenario"/> for the TLS+SCRAM variant used against the kind/cloud charts.
/// </summary>
public sealed class WritePathScenario(
    LoadTestConfig config,
    EntityCoordinator<BenchmarkArticle> articles,
    EntityCoordinator<BenchmarkAuthor>  authors,
    EntityCoordinator<BenchmarkTag>     tags,
    ILogger<WritePathScenario>          logger)
{
    public Task RunAsync(CommandFlags flags, CancellationToken ct = default) =>
        WritePathRunner.RunAsync(config, articles, authors, tags, logger, flags, applyKafkaSecurity: null, ct);
}
