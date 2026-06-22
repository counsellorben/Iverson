namespace Iverson.Events;

[Flags]
public enum StoreTarget
{
    None             = 0,
    Record           = 1 << 0,  // PostgreSQL — system of record
    Engagement       = 1 << 1,  // Elasticsearch — index this entity's own document
    Intelligence     = 1 << 2,  // Qdrant — ingest vector/chunk fields
    EngagementFanout = 1 << 3,  // Elasticsearch — re-index dependent documents that embed this entity

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
