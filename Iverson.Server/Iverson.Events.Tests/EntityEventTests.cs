using System.Text.Json;
using FluentAssertions;
using Iverson.Events;
using Xunit;

namespace Iverson.Events.Tests;

public sealed class EntityEventTests
{
    [Fact]
    public void EntityEvent_Roundtrips_ThroughJsonSerialization()
    {
        var original = new EntityEvent(
            EventType: EntityEventType.Created,
            TypeName: "Player",
            Key: "player-42",
            PayloadJson: """{"name":"Allen Iverson"}""",
            TraceId: "trace-abc",
            SchemaVersion: "1.0",
            OccurredAt: new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero),
            TargetStores: StoreTarget.All);

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<EntityEvent>(json);

        deserialized.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void StoreTarget_All_IncludesEngagement()
    {
        (StoreTarget.All & StoreTarget.Engagement).Should().Be(StoreTarget.Engagement);
    }

    [Fact]
    public void StoreTarget_All_IncludesIntelligence()
    {
        (StoreTarget.All & StoreTarget.Intelligence).Should().Be(StoreTarget.Intelligence);
    }

    [Fact]
    public void EntityTopics_Events_HasExpectedValue()
    {
        EntityTopics.Events.Should().Be("iverson.entity.events");
    }

    [Fact]
    public void EntityTopics_Dlq_HasExpectedValue()
    {
        EntityTopics.Dlq.Should().Be("iverson.entity.dlq");
    }
}
