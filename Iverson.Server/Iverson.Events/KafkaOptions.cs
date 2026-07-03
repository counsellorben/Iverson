namespace Iverson.Events;

/// <summary>
/// Configuration bound from the "Kafka" section, e.g. via env vars
/// Kafka__BootstrapServers, Kafka__SecurityProtocol, Kafka__SaslMechanism,
/// Kafka__SaslUsername, Kafka__SaslPassword, Kafka__SslCaLocation.
/// The security-related properties are all optional; when unset the client
/// connects without SASL/SSL (current PLAINTEXT local/docker-compose behavior).
/// </summary>
public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>e.g. "SASL_SSL", "PLAINTEXT", "SSL", "SASL_PLAINTEXT". Maps to <see cref="Confluent.Kafka.SecurityProtocol"/>.</summary>
    public string? SecurityProtocol { get; set; }

    /// <summary>e.g. "SCRAM-SHA-512", "PLAIN". Maps to <see cref="Confluent.Kafka.SaslMechanism"/>.</summary>
    public string? SaslMechanism { get; set; }

    public string? SaslUsername { get; set; }

    public string? SaslPassword { get; set; }

    public string? SslCaLocation { get; set; }
}
