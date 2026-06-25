using Iverson.Events;

namespace Iverson.Api.Schema;

/// <summary>
/// Decides which backing stores an entity's events should target, based purely on
/// its schema shape (not per-instance graph state). Record (Postgres) is always
/// targeted. Engagement (StarRocks) is added only when the entity can be
/// represented as a single flat row — every relation is ManyToOne/OneToOne, or a
/// ManyToMany whose foreign key lives on this row; a OneToMany disqualifies it.
/// Intelligence (Qdrant) is added when the schema has vector or chunk fields.
/// </summary>
internal static class StoreTargeting
{
    internal static StoreTarget DetermineTargetStores(SchemaDescriptor schema)
    {
        var stores = StoreTarget.Record;
        if (IsCompleteForIngestion(schema)) stores |= StoreTarget.Engagement;
        if (HasVectorOrChunkFields(schema)) stores |= StoreTarget.Intelligence;
        return stores;
    }

    internal static bool IsCompleteForIngestion(SchemaDescriptor schema) =>
        schema.Relations.All(r => r.Kind switch
        {
            RelationKind.ManyToOne  => true,
            RelationKind.OneToOne   => true,
            RelationKind.OneToMany  => false,
            RelationKind.ManyToMany => schema.FkColumns.Any(fk =>
                string.Equals(fk.ColumnName, r.ForeignKey, StringComparison.OrdinalIgnoreCase)),
            _                       => false
        });

    internal static bool HasVectorOrChunkFields(SchemaDescriptor schema) =>
        schema.VectorFields.Count > 0 || schema.ChunkFields.Count > 0;
}
