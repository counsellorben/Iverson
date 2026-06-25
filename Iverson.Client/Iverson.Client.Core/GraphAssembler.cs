using System.Collections;
using System.Reflection;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging;

namespace Iverson.Client.Core;

/// <summary>
/// Populates navigation properties on a POCO by issuing follow-up retrieval
/// calls based on relationship metadata from the EntityRegistry.
/// </summary>
public sealed class GraphAssembler(
    ObjectRetrievalService.ObjectRetrievalServiceClient retrieval,
    EntityRegistry registry,
    ILogger<GraphAssembler> logger)
{
    // Cached reflection reference to StructConverter.FromStruct<T>
    private static readonly MethodInfo _fromStructMethod =
        typeof(StructConverter).GetMethod(nameof(StructConverter.FromStruct),
            BindingFlags.Public | BindingFlags.Static)!;

    public async Task AssembleAsync<T>(T entity, CancellationToken ct = default) where T : class
        => await AssembleAsync(entity, registry.Get<T>(), ct);

    private async Task AssembleAsync(object entity, EntityDescriptor descriptor, CancellationToken ct)
    {
        foreach (var relation in descriptor.Relations)
        {
            try
            {
                switch (relation.Kind)
                {
                    case RelationKind.OneToOne:
                    case RelationKind.ManyToOne:
                        await AssembleSingle(entity, descriptor, relation, ct);
                        break;
                    case RelationKind.OneToMany:
                    case RelationKind.ManyToMany:
                        await AssembleCollection(entity, descriptor, relation, ct);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to assemble {Kind} relation {Property} on {Entity}",
                    relation.Kind, relation.Property.Name, descriptor.EntityName);
            }
        }
    }

    // OneToOne / ManyToOne: read FK from this entity, fetch one related entity
    private async Task AssembleSingle(object entity, EntityDescriptor descriptor,
        RelationDescriptor relation, CancellationToken ct)
    {
        var fkName = relation.ForeignKey ?? $"{relation.RelatedType.Name}Id";
        var fkProp = descriptor.EntityType
            .GetProperty(fkName, BindingFlags.Public | BindingFlags.Instance);

        if (fkProp is null)
        {
            logger.LogDebug("FK property '{Fk}' not found on {Type}, skipping", fkName, descriptor.EntityName);
            return;
        }

        var fkValue = fkProp.GetValue(entity)?.ToString();
        if (string.IsNullOrEmpty(fkValue)) return;

        var relatedDescriptor = registry.Get(relation.RelatedType);
        var response = await retrieval.GetAsync(new RetrievalRequest
        {
            TypeName = relatedDescriptor.EntityName,
            Key      = fkValue
        }, cancellationToken: ct);

        if (!response.Found) return;

        var related = DeserializeStruct(response.Data, relation.RelatedType);
        if (related is not null)
            relation.Property.SetValue(entity, related);
    }

    // OneToMany / ManyToMany: fetch a collection of related entities using ids from the payload
    private async Task AssembleCollection(object entity, EntityDescriptor descriptor,
        RelationDescriptor relation, CancellationToken ct)
    {
        var relatedDescriptor = registry.Get(relation.RelatedType);
        var payloadStruct     = StructConverter.ToStruct(entity);

        var joinKey = relation.Kind == RelationKind.ManyToMany
            ? (relation.ForeignKey ?? $"{relation.RelatedType.Name}Ids")
            : (relation.ForeignKey ?? $"{descriptor.EntityName}Ids");

        var keys = StructConverter.GetStringList(payloadStruct, joinKey);
        if (keys.Count == 0)
        {
            logger.LogDebug("No '{Key}' ids in payload for {Kind} {Prop}, skipping",
                joinKey, relation.Kind, relation.Property.Name);
            return;
        }

        var request = new RetrievalManyRequest { TypeName = relatedDescriptor.EntityName };
        request.Keys.AddRange(keys);

        var collectionType = typeof(List<>).MakeGenericType(relation.RelatedType);
        var collection     = (IList)Activator.CreateInstance(collectionType)!;

        var stream = retrieval.GetMany(request, cancellationToken: ct);
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            if (!response.Found) continue;
            var item = DeserializeStruct(response.Data, relation.RelatedType);
            if (item is not null) collection.Add(item);
        }

        relation.Property.SetValue(entity, collection);
    }

    public async Task AssembleManyAsync<T>(IReadOnlyList<T> entities, CancellationToken ct = default)
        where T : class
    {
        if (entities.Count == 0) return;
        var descriptor = registry.Get<T>();
        foreach (var relation in descriptor.Relations)
        {
            switch (relation.Kind)
            {
                case RelationKind.OneToOne:
                case RelationKind.ManyToOne:
                    await BatchAssembleSingleAsync(entities, descriptor, relation, ct);
                    break;
                case RelationKind.OneToMany:
                case RelationKind.ManyToMany:
                    await BatchAssembleCollectionAsync(entities, descriptor, relation, ct);
                    break;
            }
        }
    }

    private async Task BatchAssembleSingleAsync<T>(
        IReadOnlyList<T> entities,
        EntityDescriptor descriptor,
        RelationDescriptor relation,
        CancellationToken ct) where T : class
    {
        var fkName = relation.ForeignKey ?? $"{relation.RelatedType.Name}Id";
        var fkProp = descriptor.EntityType.GetProperty(fkName, BindingFlags.Public | BindingFlags.Instance);
        if (fkProp is null) return;

        // Collect FK values, remembering which entities need each value
        var fkToEntities = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            var fkValue = fkProp.GetValue(entity)?.ToString();
            if (string.IsNullOrEmpty(fkValue)) continue;
            if (!fkToEntities.TryGetValue(fkValue, out var list))
                fkToEntities[fkValue] = list = new List<T>();
            list.Add(entity);
        }

        if (fkToEntities.Count == 0) return;

        var relatedDescriptor = registry.Get(relation.RelatedType);
        var request = new RetrievalManyRequest { TypeName = relatedDescriptor.EntityName };
        request.Keys.AddRange(fkToEntities.Keys);

        var stream = retrieval.GetMany(request, new CallOptions(cancellationToken: ct));
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            if (!response.Found) continue;
            var related = DeserializeStruct(response.Data, relation.RelatedType);
            if (related is null) continue;

            // Use KeyProperty to find the key of the fetched related entity
            var relatedKey = relatedDescriptor.KeyProperty.GetValue(related)?.ToString();
            if (relatedKey is null || !fkToEntities.TryGetValue(relatedKey, out var ents)) continue;

            foreach (var ent in ents)
            {
                var perEntityRelated = DeserializeStruct(response.Data, relation.RelatedType);
                if (perEntityRelated is not null)
                    relation.Property.SetValue(ent, perEntityRelated);
            }
        }
    }

    private async Task BatchAssembleCollectionAsync<T>(
        IReadOnlyList<T> entities,
        EntityDescriptor descriptor,
        RelationDescriptor relation,
        CancellationToken ct) where T : class
    {
        var relatedDescriptor = registry.Get(relation.RelatedType);
        var joinKey = relation.Kind == RelationKind.ManyToMany
            ? (relation.ForeignKey ?? $"{relation.RelatedType.Name}Ids")
            : (relation.ForeignKey ?? $"{descriptor.EntityName}Ids");

        // Collect all FK keys across all entities; track which entities own each key
        var keyToEntityIndices = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var allKeys = new List<string>();

        for (var i = 0; i < entities.Count; i++)
        {
            var payload = StructConverter.ToStruct(entities[i]);
            var keys    = StructConverter.GetStringList(payload, joinKey);
            foreach (var k in keys)
            {
                if (!keyToEntityIndices.TryGetValue(k, out var idxList))
                {
                    keyToEntityIndices[k] = idxList = new List<int>();
                    allKeys.Add(k);
                }
                idxList.Add(i);
            }
        }

        if (allKeys.Count == 0) return;

        // One GetMany call for all keys
        var request = new RetrievalManyRequest { TypeName = relatedDescriptor.EntityName };
        request.Keys.AddRange(allKeys);

        // Build a per-entity bucket
        var buckets = new List<List<object>>(entities.Count);
        for (var i = 0; i < entities.Count; i++) buckets.Add(new List<object>());

        var stream = retrieval.GetMany(request, new CallOptions(cancellationToken: ct));
        await foreach (var response in stream.ResponseStream.ReadAllAsync(ct))
        {
            if (!response.Found) continue;

            var itemKey = relatedDescriptor.KeyProperty.GetValue(
                DeserializeStruct(response.Data, relation.RelatedType))?.ToString();
            if (itemKey is null || !keyToEntityIndices.TryGetValue(itemKey, out var ownerIndices)) continue;
            foreach (var idx in ownerIndices)
                buckets[idx].Add(DeserializeStruct(response.Data, relation.RelatedType)!);
        }

        // Set collection properties
        var collectionType = typeof(List<>).MakeGenericType(relation.RelatedType);
        for (var i = 0; i < entities.Count; i++)
        {
            if (buckets[i].Count == 0) continue;
            var collection = (IList)Activator.CreateInstance(collectionType)!;
            foreach (var item in buckets[i]) collection.Add(item);
            relation.Property.SetValue(entities[i], collection);
        }
    }

    // Calls StructConverter.FromStruct<T> via reflection when T is only known at runtime
    private static object? DeserializeStruct(Struct? data, System.Type targetType)
    {
        if (data is null) return null;
        return _fromStructMethod.MakeGenericMethod(targetType).Invoke(null, [data]);
    }
}
