namespace Iverson.Events;

[Flags]
public enum StoreTarget
{
    None         = 0,
    Engagement   = 1 << 1,  // StarRocks — engagement read store
    Intelligence = 1 << 2,  // Qdrant — vector/chunk fields

    All = Engagement | Intelligence
}

public enum EntityEventType
{
    Created,
    Updated,
    Deleted
}

public sealed record EntityEvent(
    EntityEventType EventType,
    string          TypeName,
    string          Key,
    string          PayloadJson,
    string          TraceId,
    string          SchemaVersion,
    DateTimeOffset  OccurredAt,
    StoreTarget     TargetStores = StoreTarget.All);

public static class EntityTopics
{
    public const string Events = "iverson.entity.events";
    public const string Dlq    = "iverson.entity.dlq";
}
