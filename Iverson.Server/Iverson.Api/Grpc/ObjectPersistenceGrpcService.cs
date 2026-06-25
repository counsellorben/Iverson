using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using SchemaRelKind       = Iverson.Api.Schema.RelationKind;
using SchemaRelDescriptor = Iverson.Api.Schema.RelationDescriptor;
using Iverson.Events;
using Microsoft.Extensions.Logging;

namespace Iverson.Api.Grpc;

/// <summary>
/// Lightweight write path. Stamps a server-generated UUID v7 key when the
/// client sends an empty key, then publishes an EntityEvent to Kafka.
/// The three backing stores (Postgres, StarRocks, Qdrant) consume
/// the event independently via their own consumer groups.
/// </summary>
public sealed class ObjectPersistenceGrpcService(
    IEventProducer events,
    SchemaRegistry registry,
    ILogger<ObjectPersistenceGrpcService> logger)
    : ObjectPersistenceService.ObjectPersistenceServiceBase
{
    private const string SchemaVersion = "1";

    public override async Task<PersistResponse> Post(
        PersistRequest request, ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        ValidateRelations(request.Payload, schema);

        var targetStores = StoreTarget.Record;
        if (IsCompleteForIngestion(schema))              targetStores |= StoreTarget.Engagement;
        if (HasVectorOrChunkFields(schema))              targetStores |= StoreTarget.Intelligence;

        // Resolve key: honour client-supplied key; generate UUID v7 when absent/empty
        var key = ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
        {
            key = Guid.CreateVersion7().ToString();
            SetKey(request.Payload, schema.KeyColumn.Name, key);
        }

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Persistence.Post] type={Type} key={Key} stores={Stores}",
                request.TypeName, key, targetStores);

        await events.ProduceAsync(
            EntityTopics.Created,
            key,
            new EntityEvent(
                request.TypeName,
                key,
                payloadJson,
                request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
                SchemaVersion,
                DateTimeOffset.UtcNow,
                targetStores));

        return new PersistResponse
        {
            Success = true,
            Key     = key,
            TraceId = request.TraceId
        };
    }

    public override async Task<PersistResponse> Update(
        PersistRequest request, ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var key = ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Update requires a non-empty '{schema.KeyColumn.Name}' in the payload."));

        ValidateRelations(request.Payload, schema);

        var targetStores = StoreTarget.Record;
        if (IsCompleteForIngestion(schema))              targetStores |= StoreTarget.Engagement;
        if (HasVectorOrChunkFields(schema))              targetStores |= StoreTarget.Intelligence;

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Persistence.Update] type={Type} key={Key} stores={Stores}",
                request.TypeName, key, targetStores);

        await events.ProduceAsync(
            EntityTopics.Updated,
            key,
            new EntityEvent(
                request.TypeName,
                key,
                payloadJson,
                request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
                SchemaVersion,
                DateTimeOffset.UtcNow,
                targetStores));

        return new PersistResponse
        {
            Success = true,
            Key     = key,
            TraceId = request.TraceId
        };
    }

    // ── Relation validation ────────────────────────────────────────────────────

    private void ValidateRelations(Struct payload, SchemaDescriptor schema)
    {
        var errors = new List<string>();

        foreach (var relation in schema.Relations)
        {
            switch (relation.Kind)
            {
                case SchemaRelKind.ManyToOne:
                case SchemaRelKind.OneToOne:
                    ValidateSingleRelation(payload, relation, schema, errors);
                    break;

                case SchemaRelKind.ManyToMany:
                    ValidateCollectionRelation(payload, relation, errors);
                    break;

                case SchemaRelKind.OneToMany:
                    // FK lives on the other entity — nothing to validate here
                    break;
            }
        }

        if (errors.Count > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                string.Join(" | ", errors)));
    }

    private void ValidateSingleRelation(
        Struct payload, SchemaRelDescriptor relation, SchemaDescriptor schema, List<string> errors)
    {
        // Check FK field first (e.g. "AuthorId" / "authorId")
        var fkValue = GetFieldValue(payload, relation.ForeignKey);
        if (fkValue is not null)
        {
            if (!Guid.TryParse(fkValue.StringValue, out var g) || g == Guid.Empty)
                errors.Add($"'{relation.ForeignKey}': must be a valid non-empty GUID.");
            return;
        }

        // Check nav property as embedded Struct (e.g. "Author" / "author")
        var navValue = GetFieldValue(payload, relation.PropertyName);
        if (navValue?.StructValue is { } nested)
        {
            ValidateNestedObject(nested, relation.PropertyName, relation.RelatedTypeName, errors);
            return;
        }

        // Neither present — required only when the FK column is non-nullable
        var fkCol = schema.ScalarColumns.FirstOrDefault(c =>
            string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase));

        if (fkCol is null || !fkCol.IsNullable)
            errors.Add(
                $"Relation '{relation.PropertyName}' ({relation.Kind}) is required. " +
                $"Provide '{relation.ForeignKey}' (GUID reference) or " +
                $"'{relation.PropertyName}' (embedded object).");
    }

    private void ValidateCollectionRelation(
        Struct payload, SchemaRelDescriptor relation, List<string> errors)
    {
        // FK array field (e.g. "TagIds" / "tagIds")
        var fkValue = GetFieldValue(payload, relation.ForeignKey);
        if (fkValue?.ListValue is { } fkList)
        {
            for (var i = 0; i < fkList.Values.Count; i++)
            {
                var str = fkList.Values[i].StringValue;
                if (!Guid.TryParse(str, out var g) || g == Guid.Empty)
                    errors.Add($"'{relation.ForeignKey}[{i}]': invalid GUID '{str}'.");
            }
            return;
        }

        // Nav property as array of Structs (e.g. "Tags": [...])
        var navValue = GetFieldValue(payload, relation.PropertyName);
        if (navValue?.ListValue is { } navList)
        {
            for (var i = 0; i < navList.Values.Count; i++)
            {
                var item = navList.Values[i].StructValue;
                if (item is null)
                {
                    errors.Add($"'{relation.PropertyName}[{i}]': expected an object, got a scalar.");
                    continue;
                }
                ValidateNestedObject(item, $"{relation.PropertyName}[{i}]", relation.RelatedTypeName, errors);
            }
        }
        // Neither present — collection relations are optional (empty collection is valid)
    }

    /// <summary>
    /// Validates a nested Struct that represents a related entity.
    /// If the nested object has a non-empty key it is treated as an existing entity
    /// and must contain only that key field — no extra properties allowed.
    /// </summary>
    private void ValidateNestedObject(Struct nested, string path, string relatedTypeName, List<string> errors)
    {
        var relatedSchema  = registry.Get(relatedTypeName);
        var keyColumnName  = relatedSchema?.KeyColumn.Name ?? "Id";
        var nestedKeyValue = GetFieldValue(nested, keyColumnName);
        var nestedKey      = nestedKeyValue?.StringValue;

        var isExistingEntity = !string.IsNullOrWhiteSpace(nestedKey)
                            && nestedKey != Guid.Empty.ToString()
                            && Guid.TryParse(nestedKey, out _);

        if (isExistingEntity && nested.Fields.Count > 1)
            errors.Add(
                $"'{path}': existing entity (key='{nestedKey}') must only include " +
                $"the key field '{keyColumnName}' — remove extra properties.");
    }

    // ── Key helpers ───────────────────────────────────────────────────────────

    private static string ExtractKey(Struct payload, string keyColumn)
    {
        foreach (var candidate in Candidates(keyColumn))
            if (payload.Fields.TryGetValue(candidate, out var v))
                return v.StringValue;
        return string.Empty;
    }

    private static void SetKey(Struct payload, string keyColumn, string key)
    {
        foreach (var candidate in Candidates(keyColumn))
            if (payload.Fields.ContainsKey(candidate))
            {
                payload.Fields[candidate] = Value.ForString(key);
                return;
            }
        payload.Fields[keyColumn] = Value.ForString(key);
    }

    private static Value? GetFieldValue(Struct s, string fieldName)
    {
        foreach (var candidate in Candidates(fieldName))
            if (s.Fields.TryGetValue(candidate, out var v))
                return v;
        return null;
    }

    // Tries both the canonical casing and camelCase (client sends camelCase)
    private static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        yield return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static bool HasVectorOrChunkFields(SchemaDescriptor schema) =>
        schema.VectorFields.Count > 0 || schema.ChunkFields.Count > 0;

    private static bool IsCompleteForIngestion(SchemaDescriptor schema) =>
        schema.Relations.All(r => r.Kind switch
        {
            SchemaRelKind.ManyToOne  => true,
            SchemaRelKind.OneToOne   => true,
            SchemaRelKind.OneToMany  => false,
            SchemaRelKind.ManyToMany => schema.FkColumns.Any(fk =>
                string.Equals(fk.ColumnName, r.ForeignKey, StringComparison.OrdinalIgnoreCase)),
            _                        => false
        });

    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));
}

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
