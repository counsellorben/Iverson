using Iverson.Client.Core;
using Iverson.Events;
using Iverson.LoadTest.Auth;
using Iverson.LoadTest.Entities;
using Microsoft.Extensions.Logging;

namespace Iverson.LoadTest.Scenarios;

/// <summary>
/// write-path benchmark against the kind/cloud Helm charts' Kafka, which (unlike docker-compose)
/// only exposes a TLS + SCRAM-SHA-512 listener (see charts/kafka/templates/kafka.yaml). Reuses
/// Iverson.Events' <see cref="KafkaClientConfigFactory"/> — the same code the API deployment's
/// security settings are meant to be paired with — instead of re-deriving SASL/SSL wiring here.
/// </summary>
public sealed class KindWritePathScenario(
    LoadTestConfig config,
    KafkaOptions kafkaOptions,
    EntityCoordinator<BenchmarkArticle> articles,
    EntityCoordinator<BenchmarkAuthor>  authors,
    EntityCoordinator<BenchmarkTag>     tags,
    ActingUserIdentities                identities,
    ILogger<KindWritePathScenario>      logger)
{
    public Task RunAsync(CommandFlags flags, CancellationToken ct = default) =>
        WritePathRunner.RunAsync(config, articles, authors, tags, identities, logger, flags,
            applyKafkaSecurity: c => KafkaClientConfigFactory.ApplySecurity(c, kafkaOptions), ct);
}
