using Iverson.Events;

namespace Iverson.Api.Schema;

/// <summary>
/// Decides which backing stores an entity's events should target, based purely on
/// its schema shape (not per-instance graph state).
///
/// Engagement (StarRocks): eligible when every relation is ManyToOne/OneToOne, or a
/// ManyToMany whose FK lives on this row. A OneToMany disqualifies the entity because
/// it logically owns a collection with no representation in a single flat row. Note
/// that large-field columns (Body, embeddings) are excluded from the StarRocks schema
/// separately in ToStarRocksTableSchema — that is an orthogonal concern.
///
/// Intelligence (Qdrant): eligible when the schema declares vector or chunk fields.
/// </summary>
internal static class StoreTargeting
{
    internal static StoreTarget DetermineTargetStores(SchemaDescriptor schema)
    {
        var stores = StoreTarget.None;
        if (IsEngagementEligible(schema)) stores |= StoreTarget.Engagement;
        if (HasVectorOrChunkFields(schema)) stores |= StoreTarget.Intelligence;
        return stores;
    }

    internal static bool IsEngagementEligible(SchemaDescriptor schema) =>
        schema.Relations.All(r => r.Kind switch
        {
            RelationKind.ManyToOne  => true,
            RelationKind.OneToOne   => true,
            RelationKind.OneToMany  => false,
            RelationKind.ManyToMany => schema.FkColumns
                .Any(fk => string.Equals(
                    fk.ColumnName,
                    r.ForeignKey,
                    StringComparison.OrdinalIgnoreCase))
        });

    internal static bool HasVectorOrChunkFields(SchemaDescriptor schema) =>
        schema.VectorFields.Count > 0 || schema.ChunkFields.Count > 0;
}
