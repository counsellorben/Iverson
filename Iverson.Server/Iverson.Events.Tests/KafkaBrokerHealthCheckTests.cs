using Confluent.Kafka;
using FluentAssertions;
using Iverson.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Events.Tests;

/// <summary>
/// CSR finding #7: <c>/health</c>'s Kafka check must be passive. <see cref="KafkaBrokerHealthCheck"/>
/// replaces the old <c>IEventProducer.ProduceAsync</c> call with <c>IAdminClient.GetMetadata</c> —
/// these tests pin that it calls ONLY <c>GetMetadata</c> and never any produce/create/topic
/// operation on the admin client.
/// </summary>
public sealed class KafkaBrokerHealthCheckTests
{
    private static (KafkaBrokerHealthCheck healthCheck, IAdminClient adminClient) Create()
    {
        var adminClient = Substitute.For<IAdminClient>();
        var healthCheck = new KafkaBrokerHealthCheck(adminClient, NullLogger<KafkaBrokerHealthCheck>.Instance);
        return (healthCheck, adminClient);
    }

    [Fact]
    public async Task PingAsync_WhenGetMetadataSucceeds_ReturnsTrue()
    {
        var (healthCheck, adminClient) = Create();
        adminClient.GetMetadata(Arg.Any<TimeSpan>()).Returns(new Metadata(
            new List<BrokerMetadata>(), new List<TopicMetadata>(), 0, "broker"));

        var result = await healthCheck.PingAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task PingAsync_CallsGetMetadataOnly_NeverProducesOrCreatesATopic()
    {
        var (healthCheck, adminClient) = Create();
        adminClient.GetMetadata(Arg.Any<TimeSpan>()).Returns(new Metadata(
            new List<BrokerMetadata>(), new List<TopicMetadata>(), 0, "broker"));

        await healthCheck.PingAsync();

        adminClient.Received(1).GetMetadata(Arg.Any<TimeSpan>());
        await adminClient.DidNotReceive().CreateTopicsAsync(
            Arg.Any<IEnumerable<Confluent.Kafka.Admin.TopicSpecification>>(),
            Arg.Any<Confluent.Kafka.Admin.CreateTopicsOptions>());
    }

    [Fact]
    public async Task PingAsync_WhenGetMetadataThrows_ReturnsFalse_RatherThanThrowing()
    {
        var (healthCheck, adminClient) = Create();
        adminClient.GetMetadata(Arg.Any<TimeSpan>()).Throws(new KafkaException(ErrorCode.Local_Transport));

        var result = await healthCheck.PingAsync();

        result.Should().BeFalse();
    }
}
