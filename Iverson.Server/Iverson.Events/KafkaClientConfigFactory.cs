using Confluent.Kafka;

namespace Iverson.Events;

/// <summary>
/// Applies optional SASL/SSL security settings from <see cref="KafkaOptions"/> onto any
/// <see cref="ClientConfig"/>-derived object (ProducerConfig, ConsumerConfig, AdminClientConfig).
/// When the security fields are absent/empty, the config object is left untouched — this
/// preserves the current PLAINTEXT behavior for local/docker-compose environments.
/// </summary>
public static class KafkaClientConfigFactory
{
    public static void ApplySecurity(ClientConfig config, KafkaOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SecurityProtocol))
        {
            config.SecurityProtocol = ParseEnum<SecurityProtocol>(
                options.SecurityProtocol, nameof(KafkaOptions.SecurityProtocol));
        }

        if (!string.IsNullOrWhiteSpace(options.SaslMechanism))
        {
            config.SaslMechanism = ParseEnum<SaslMechanism>(
                options.SaslMechanism, nameof(KafkaOptions.SaslMechanism));
        }

        if (!string.IsNullOrWhiteSpace(options.SaslUsername))
        {
            config.SaslUsername = options.SaslUsername;
        }

        if (!string.IsNullOrWhiteSpace(options.SaslPassword))
        {
            config.SaslPassword = options.SaslPassword;
        }

        if (!string.IsNullOrWhiteSpace(options.SslCaLocation))
        {
            config.SslCaLocation = options.SslCaLocation;
        }
    }

    private static T ParseEnum<T>(string value, string fieldName) where T : struct, Enum
    {
        // Confluent.Kafka's enum names ("SaslSsl", "ScramSha512") drop separators that
        // convention uses in config values ("SASL_SSL", "SCRAM-SHA-512"), so normalize
        // both before comparing case-insensitively.
        var normalized = value.Replace("_", "").Replace("-", "");
        if (Enum.TryParse<T>(normalized, ignoreCase: true, out var result))
        {
            return result;
        }

        throw new InvalidOperationException(
            $"Invalid Kafka {fieldName} value '{value}'. Valid values: {string.Join(", ", Enum.GetNames<T>())}");
    }
}
