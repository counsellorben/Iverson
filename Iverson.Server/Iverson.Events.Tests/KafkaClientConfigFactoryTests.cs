using Confluent.Kafka;
using FluentAssertions;
using Iverson.Events;
using Xunit;

namespace Iverson.Events.Tests;

public sealed class KafkaClientConfigFactoryTests
{
    private static KafkaOptions SecureOptions() => new()
    {
        BootstrapServers = "broker:9093",
        SecurityProtocol = "SASL_SSL",
        SaslMechanism = "SCRAM-SHA-512",
        SaslUsername = "iverson",
        SaslPassword = "s3cr3t",
        SslCaLocation = "/etc/ssl/certs/kafka-ca.pem"
    };

    private static void AssertSecurityApplied(ClientConfig config)
    {
        config.SecurityProtocol.Should().Be(SecurityProtocol.SaslSsl);
        config.SaslMechanism.Should().Be(SaslMechanism.ScramSha512);
        config.SaslUsername.Should().Be("iverson");
        config.SaslPassword.Should().Be("s3cr3t");
        config.SslCaLocation.Should().Be("/etc/ssl/certs/kafka-ca.pem");
    }

    private static void AssertSecurityAbsent(ClientConfig config)
    {
        config.SecurityProtocol.Should().BeNull();
        config.SaslMechanism.Should().BeNull();
        config.SaslUsername.Should().BeNull();
        config.SaslPassword.Should().BeNull();
        config.SslCaLocation.Should().BeNull();
    }

    [Fact]
    public void ApplySecurity_NoSecurityConfigured_LeavesProducerConfigUnset()
    {
        var config = new ProducerConfig { BootstrapServers = "localhost:9092" };

        KafkaClientConfigFactory.ApplySecurity(config, new KafkaOptions { BootstrapServers = "localhost:9092" });

        AssertSecurityAbsent(config);
        config.BootstrapServers.Should().Be("localhost:9092");
    }

    [Fact]
    public void ApplySecurity_NoSecurityConfigured_LeavesConsumerConfigUnset()
    {
        var config = new ConsumerConfig { BootstrapServers = "localhost:9092" };

        KafkaClientConfigFactory.ApplySecurity(config, new KafkaOptions { BootstrapServers = "localhost:9092" });

        AssertSecurityAbsent(config);
    }

    [Fact]
    public void ApplySecurity_NoSecurityConfigured_LeavesAdminClientConfigUnset()
    {
        var config = new AdminClientConfig { BootstrapServers = "localhost:9092" };

        KafkaClientConfigFactory.ApplySecurity(config, new KafkaOptions { BootstrapServers = "localhost:9092" });

        AssertSecurityAbsent(config);
    }

    [Fact]
    public void ApplySecurity_SaslSslScramSha512Configured_SetsProducerConfig()
    {
        var config = new ProducerConfig { BootstrapServers = "broker:9093" };

        KafkaClientConfigFactory.ApplySecurity(config, SecureOptions());

        AssertSecurityApplied(config);
    }

    [Fact]
    public void ApplySecurity_SaslSslScramSha512Configured_SetsConsumerConfig()
    {
        var config = new ConsumerConfig { BootstrapServers = "broker:9093" };

        KafkaClientConfigFactory.ApplySecurity(config, SecureOptions());

        AssertSecurityApplied(config);
    }

    [Fact]
    public void ApplySecurity_SaslSslScramSha512Configured_SetsAdminClientConfig()
    {
        var config = new AdminClientConfig { BootstrapServers = "broker:9093" };

        KafkaClientConfigFactory.ApplySecurity(config, SecureOptions());

        AssertSecurityApplied(config);
    }

    [Fact]
    public void ApplySecurity_UnrecognizedSecurityProtocol_ThrowsInvalidOperationException()
    {
        var config = new ProducerConfig();
        var options = new KafkaOptions { SecurityProtocol = "NOT_A_REAL_PROTOCOL" };

        var act = () => KafkaClientConfigFactory.ApplySecurity(config, options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NOT_A_REAL_PROTOCOL*");
    }

    [Fact]
    public void ApplySecurity_UnrecognizedSaslMechanism_ThrowsInvalidOperationException()
    {
        var config = new ProducerConfig();
        var options = new KafkaOptions { SaslMechanism = "NOT_A_REAL_MECHANISM" };

        var act = () => KafkaClientConfigFactory.ApplySecurity(config, options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NOT_A_REAL_MECHANISM*");
    }
}
