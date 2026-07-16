using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.Kafka;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public sealed class KafkaOrderingContainerFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.6.0")
        .Build();

    public string BootstrapAddress { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        BootstrapAddress = _container.GetBootstrapAddress();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// Regression test for the cross-topic consumer race (system-architectural-review Critical
/// Finding 1): before the single-topic collapse, a Created and a Deleted for the same key could
/// be consumed out of order because they rode two independently-polling Kafka subscriptions (two
/// separate consumer groups). Against the collapsed <see cref="EntityTopics.Events"/> topic, both
/// events share one partition (same message key, Kafka's default partitioner) and one consumer
/// loop, so Kafka's per-partition ordering guarantee now actually applies. Proven end-to-end
/// against a real broker — not achievable meaningfully against the old 3-topic layout, which is
/// itself evidence of the gap this closes.
/// </summary>
public sealed class EngagementStoreConsumerKafkaOrderingTests(KafkaOrderingContainerFixture fixture)
    : IClassFixture<KafkaOrderingContainerFixture>
{
    [Fact]
    public async Task CreatedThenDeleted_SameKey_AppliedInProducedOrder()
    {
        var bootstrap = fixture.BootstrapAddress;

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrap }).Build();
        try
        {
            await admin.CreateTopicsAsync(
                [new TopicSpecification { Name = EntityTopics.Events, NumPartitions = 12, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // already created by a previous test run against the same container instance
        }

        using var kafkaProducer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = bootstrap }).Build();
        var producer = new KafkaProducer(kafkaProducer, NullLogger<KafkaProducer>.Instance);

        var key = Guid.NewGuid().ToString();
        var payload = $$"""{"Id":"{{key}}","Name":"Alice"}""";

        var createdEvent = new EntityEvent(
            EntityEventType.Created, "Author", key, payload,
            "trace-created", "1", DateTimeOffset.UtcNow, StoreTarget.Engagement);
        var deletedEvent = new EntityEvent(
            EntityEventType.Deleted, "Author", key, payload,
            "trace-deleted", "1", DateTimeOffset.UtcNow, StoreTarget.Engagement);

        // Produce Created then Deleted BEFORE the consumer ever subscribes. The consumer group
        // below is brand new (AutoOffsetReset.Earliest, never-before-seen GroupId), so it will
        // replay both messages from offset 0 in the order Kafka appended them to the partition —
        // this is what actually proves ordering, independent of any produce/subscribe race.
        await producer.ProduceAsync(EntityTopics.Events, key, createdEvent);
        await producer.ProduceAsync(EntityTopics.Events, key, deletedEvent);

        var sr = Substitute.For<IEngagementStoreEntityStore>();
        sr.UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        sr.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        var registry = new SchemaRegistry(new SchemaRegistryRepository(sql), NullLogger<SchemaRegistry>.Instance);
        await registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var options    = new KafkaOptions { BootstrapServers = bootstrap };
        var dispatcher = new MessageDispatcher(kafkaProducer, NullLogger<MessageDispatcher>.Instance);
        var consumer = new KafkaConsumer(
            options,
            NullLogger<KafkaConsumer>.Instance,
            dispatcher,
            cfg => new ConsumerBuilder<string, string>(cfg).Build(),
            cfg => new AdminClientBuilder(cfg).Build());

        // SchemaFixtures.AuthorSchema() uses BypassAuthorization (OwnerField == null), so the owner
        // re-derivation path is never exercised here — no stubbing needed on this substitute.
        var entities = Substitute.For<IEntityRepository>();
        var sut = new EngagementStoreConsumer(consumer, sr, registry, entities, NullLogger<EngagementStoreConsumer>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => sr.ReceivedCalls().Count(c =>
                    c.GetMethodInfo().Name is nameof(IEngagementStoreEntityStore.UpsertAsync)
                                            or nameof(IEngagementStoreEntityStore.DeleteAsync)) >= 2,
                TimeSpan.FromSeconds(20));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        Received.InOrder(() =>
        {
            sr.UpsertAsync(Arg.Any<StarRocksTableSchema>(), Arg.Any<string>());
            sr.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), key);
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within timeout — consumer did not process both events.");
            await Task.Delay(200);
        }
    }
}
