namespace Iverson.Events;

[Flags]
public enum StoreTarget
{
    None         = 0,
    Record       = 1 << 0,  // PostgreSQL — system of record
    Engagement   = 1 << 1,  // StarRocks — engagement read store
    Intelligence = 1 << 2,  // Qdrant — vector/chunk fields

    All = Record | Engagement | Intelligence
}

public sealed record EntityEvent(
    string         TypeName,
    string         Key,
    string         PayloadJson,
    string         TraceId,
    string         SchemaVersion,
    DateTimeOffset OccurredAt,
    StoreTarget    TargetStores = StoreTarget.All);

public static class EntityTopics
{
    public const string Created = "iverson.entity.created";
    public const string Updated = "iverson.entity.updated";
    public const string Deleted = "iverson.entity.deleted";
    public const string Dlq     = "iverson.entity.dlq";
}
