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
    public void StoreTarget_All_IncludesRecord()
    {
        (StoreTarget.All & StoreTarget.Record).Should().Be(StoreTarget.Record);
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
    public void StoreTarget_All_ExcludesEngagementFanout()
    {
        StoreTarget.All.HasFlag(StoreTarget.EngagementFanout).Should().BeFalse();
    }

    [Fact]
    public void StoreTarget_FlagsCanBeCombined()
    {
        var combined = StoreTarget.Record | StoreTarget.EngagementFanout;
        combined.HasFlag(StoreTarget.Record).Should().BeTrue();
        combined.HasFlag(StoreTarget.EngagementFanout).Should().BeTrue();
        combined.HasFlag(StoreTarget.Engagement).Should().BeFalse();
    }

    [Fact]
    public void EntityTopics_Created_HasExpectedValue()
    {
        EntityTopics.Created.Should().Be("iverson.entity.created");
    }

    [Fact]
    public void EntityTopics_Updated_HasExpectedValue()
    {
        EntityTopics.Updated.Should().Be("iverson.entity.updated");
    }

    [Fact]
    public void EntityTopics_Deleted_HasExpectedValue()
    {
        EntityTopics.Deleted.Should().Be("iverson.entity.deleted");
    }
}
