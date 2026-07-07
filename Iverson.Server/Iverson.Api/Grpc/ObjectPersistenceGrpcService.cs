using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using SchemaRelKind       = Iverson.Api.Schema.RelationKind;
using SchemaRelDescriptor = Iverson.Api.Schema.RelationDescriptor;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Grpc;

/// <summary>
/// Lightweight write path. Stamps a server-generated UUID v7 key when the
/// client sends an empty key, writes directly to Postgres, then publishes
/// an EntityEvent for StarRocks and Qdrant to consume via their consumer groups.
/// </summary>
public sealed class ObjectPersistenceGrpcService(
    IEventProducer events,
    IPostgresRepository sql,
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

        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Always assign a server-generated UUID v7; client-supplied Id is ignored
        var key = Guid.CreateVersion7().ToString();
        SetKey(request.Payload, schema.KeyColumn.Name, key);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Persistence.Post] type={Type} key={Key} stores={Stores}",
                request.TypeName, key, targetStores);

        await UpsertAndEnqueueOutboxAsync(schema, request.TypeName, key, payloadJson);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        try
        {
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
            await DeleteOutboxRowIfPresentAsync(request.TypeName, key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Persistence.Post] Opportunistic publish failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName, key);
        }

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

        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Persistence.Update] type={Type} key={Key} stores={Stores}",
                request.TypeName, key, targetStores);

        await UpsertAndEnqueueOutboxAsync(schema, request.TypeName, key, payloadJson);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        try
        {
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
            await DeleteOutboxRowIfPresentAsync(request.TypeName, key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Persistence.Update] Opportunistic publish failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName, key);
        }

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

    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private async Task UpsertAndEnqueueOutboxAsync(
        SchemaDescriptor schema, string typeName, string key, string payloadJson)
    {
        var allCols   = schema.ScalarColumns.Select(c => c.Name).ToList();
        var updateSet = allCols.Count > 0
            ? string.Join(", ", allCols.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
            : $"\"{schema.KeyColumn.Name}\" = EXCLUDED.\"{schema.KeyColumn.Name}\"";

        var upsertSql =
            $"""
            INSERT INTO "{schema.TableName}"
            SELECT * FROM json_populate_record(null::"{schema.TableName}", @Json::json)
            ON CONFLICT ("{schema.KeyColumn.Name}") DO UPDATE SET {updateSet}
            """;

        var outboxSql =
            $"""
            INSERT INTO "{ReconciliationSchema.TableName}"
                ("Id", "TypeName", "EntityKey", "EnqueuedAt", "Attempts", "LastError", "LastAttemptAt")
            VALUES
                (@Id, @TypeName, @EntityKey, @EnqueuedAt, 0, null, null)
            """;

        await sql.ExecuteInTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });
            await tx.ExecuteAsync(outboxSql, new
            {
                Id = Guid.CreateVersion7(),
                TypeName = typeName,
                EntityKey = key,
                EnqueuedAt = DateTimeOffset.UtcNow
            });
        });
    }

    private Task DeleteOutboxRowIfPresentAsync(string typeName, string key) =>
        sql.ExecuteAsync(
            $"""
            DELETE FROM "{ReconciliationSchema.TableName}"
            WHERE "TypeName" = @TypeName AND "EntityKey" = @EntityKey
            """,
            new { TypeName = typeName, EntityKey = key });
}

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
