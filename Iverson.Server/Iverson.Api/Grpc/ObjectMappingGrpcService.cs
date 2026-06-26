using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging;
using Iverson.Api;
using SchemaRelKind       = Iverson.Api.Schema.RelationKind;
using SchemaRelDescriptor = Iverson.Api.Schema.RelationDescriptor;

namespace Iverson.Api.Grpc;

/// <summary>
/// Implements full entity CRUD with server-side relationship resolution.
/// Routing to backing stores (SQL / ES / Qdrant / Kafka) is determined by
/// the server's entity schema — the client is ignorant of this mapping.
/// </summary>
public sealed class ObjectMappingGrpcService(
    IPostgresRepository _sql,
    IVectorService _vector,
    IEventProducer _events,
    SchemaRegistry _registry,
    IEmbeddingService _embedding,
    IStarRocksRepository _starRocks,
    ILogger<ObjectMappingGrpcService> _logger)
    : ObjectMappingService.ObjectMappingServiceBase
{
    private const string SchemaVersion = "1";

    // ── Schema registration ────────────────────────────────────────────────────

    public override async Task<SchemaResponse> RegisterSchema(
        SchemaRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[RegisterSchema] root={Type} dependents={Deps}",
            request.RootType?.TypeName, request.Dependents.Count);

        if (request.RootType is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "root_type is required."));

        var registered = new List<string>();

        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, _embedding);

            await _sql.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));
            await _starRocks.ApplyTableAsync(SchemaBuilder.ToStarRocksTableSchema(descriptor));

            if (descriptor.VectorFields.Count > 0)
                await _vector.ApplyCollectionAsync(SchemaBuilder.ToCollectionSchema(descriptor));

            if (descriptor.ChunkFields.Count > 0)
                await _vector.ApplyCollectionAsync(SchemaBuilder.ToChunkCollectionSchema(descriptor));

            await _registry.RegisterAsync(descriptor);
            registered.Add(descriptor.TypeName);
        }

        return new SchemaResponse
        {
            Success    = true,
            TraceId    = request.TraceId,
            Registered = { registered }
        };
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public override async Task<MappingResponse> Get(
        MappingGetRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Get] type={Type} key={Key} depth={Depth}",
            request.TypeName, request.Key, request.Depth);

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key);
        if (rowJson is null)
            return new MappingResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var entityStruct = JsonParser.Default.Parse<Struct>(rowJson);

        if (request.Depth > 0)
            await ResolveRelationsAsync(entityStruct, schema, request.Depth, context.CancellationToken);

        return new MappingResponse { Success = true, Data = entityStruct, TraceId = request.TraceId };
    }

    public override async Task<MappingResponse> Post(
        MappingWriteRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Post] type={Type}", request.TypeName);

        var schema = RequireSchema(request.TypeName);

        ValidateRelations(request.Payload, schema);

        var key = ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
        {
            key = Guid.CreateVersion7().ToString();
            SetKey(request.Payload, schema.KeyColumn.Name, key);
        }

        var payloadJson = StructSerializer.SerializePayload(request.Payload);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        await _events.ProduceAsync(
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

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingResponse> Update(
        MappingWriteRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Update] type={Type}", request.TypeName);

        var schema = RequireSchema(request.TypeName);

        var key = ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Update requires a non-empty '{schema.KeyColumn.Name}' in the payload."));

        ValidateRelations(request.Payload, schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        await _events.ProduceAsync(
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

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingDeleteResponse> Delete(
        MappingDeleteRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Delete] type={Type} key={Key}", request.TypeName, request.Key);

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key);
        if (rowJson is null)
            return new MappingDeleteResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        await _sql.ExecuteAsync(
            $"DELETE FROM \"{schema.TableName}\" WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = request.Key });

        await _events.ProduceAsync(
            EntityTopics.Deleted,
            request.Key,
            new EntityEvent(
                request.TypeName,
                request.Key,
                rowJson,
                request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
                SchemaVersion,
                DateTimeOffset.UtcNow,
                StoreTargeting.DetermineTargetStores(schema)));

        return new MappingDeleteResponse { Success = true, TraceId = request.TraceId };
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        _registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private async Task<string?> FetchByKeyAsync(SchemaDescriptor schema, string key) =>
        await _sql.QuerySingleOrDefaultAsync<string>(
            $"SELECT row_to_json(t)::text FROM \"{schema.TableName}\" t WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = key });

    // ── Relation resolution ───────────────────────────────────────────────────

    private async Task ResolveRelationsAsync(
        Struct entityStruct, SchemaDescriptor schema, int depth, CancellationToken ct)
    {
        foreach (var relation in schema.Relations)
        {
            switch (relation.Kind)
            {
                case SchemaRelKind.ManyToOne:
                case SchemaRelKind.OneToOne:
                    await ResolveSingleRelationAsync(entityStruct, relation, depth, ct);
                    break;

                case SchemaRelKind.ManyToMany:
                    await ResolveManyToManyAsync(entityStruct, relation, depth, ct);
                    break;

                case SchemaRelKind.OneToMany:
                    await ResolveOneToManyAsync(entityStruct, schema, relation, depth, ct);
                    break;
            }
        }
    }

    private async Task ResolveSingleRelationAsync(
        Struct entityStruct, SchemaRelDescriptor relation, int depth, CancellationToken ct)
    {
        var fkValue = GetFieldString(entityStruct, relation.ForeignKey);
        if (string.IsNullOrWhiteSpace(fkValue)) return;

        var relatedSchema = _registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rowJson = await FetchByKeyAsync(relatedSchema, fkValue);
        if (rowJson is null) return;

        var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);
        if (depth > 1)
            await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);

        entityStruct.Fields[relation.PropertyName] = Value.ForStruct(relatedStruct);
    }

    private async Task ResolveManyToManyAsync(
        Struct entityStruct, SchemaRelDescriptor relation, int depth, CancellationToken ct)
    {
        var ids = GetGetFieldStringList(entityStruct, relation.ForeignKey);
        if (ids.Count == 0) return;

        var relatedSchema = _registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await _sql.QueryAsync<KeyedRow>(
            $"SELECT \"{relatedSchema.KeyColumn.Name}\"::text AS key, " +
            $"row_to_json(t)::text AS data " +
            $"FROM \"{relatedSchema.TableName}\" t " +
            $"WHERE \"{relatedSchema.KeyColumn.Name}\" = ANY(@ids::uuid[])",
            new { ids = ids.ToArray() });

        var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        var items = new List<Value>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            if (!rowsByKey.TryGetValue(id, out var row)) continue;
            var relatedStruct = JsonParser.Default.Parse<Struct>(row.Data);
            if (depth > 1)
                await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }

    private async Task ResolveOneToManyAsync(
        Struct entityStruct, SchemaDescriptor schema, SchemaRelDescriptor relation, int depth, CancellationToken ct)
    {
        var keyValue = GetFieldString(entityStruct, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(keyValue)) return;

        var relatedSchema = _registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await _sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM \"{relatedSchema.TableName}\" t WHERE \"{relation.ForeignKey}\" = @Key::uuid",
            new { Key = keyValue });

        var items = new List<Value>();
        foreach (var rowJson in rows)
        {
            if (ct.IsCancellationRequested) break;
            var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);
            if (depth > 1)
                await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }

    // ── Struct field helpers ──────────────────────────────────────────────────

    private static string? GetFieldString(Struct s, string fieldName)
    {
        foreach (var name in Candidates(fieldName))
            if (s.Fields.TryGetValue(name, out var v))
                return v.StringValue;
        return null;
    }

    private static IReadOnlyList<string> GetGetFieldStringList(Struct s, string fieldName)
    {
        foreach (var name in Candidates(fieldName))
            if (s.Fields.TryGetValue(name, out var v) && v.ListValue is not null)
                return v.ListValue.Values
                    .Select(x => x.StringValue)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();
        return [];
    }

    private static Value? GetFieldValue(Struct s, string fieldName)
    {
        foreach (var name in Candidates(fieldName))
            if (s.Fields.TryGetValue(name, out var v))
                return v;
        return null;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        if (name.Length > 0)
            yield return char.ToLowerInvariant(name[0]) + name[1..];
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

    // ── Relation validation ───────────────────────────────────────────────────

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
                    break; // FK lives on the related entity
            }
        }

        if (errors.Count > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                string.Join(" | ", errors)));
    }

    private void ValidateSingleRelation(
        Struct payload, SchemaRelDescriptor relation, SchemaDescriptor schema, List<string> errors)
    {
        var fkValue = GetFieldValue(payload, relation.ForeignKey);
        if (fkValue is not null)
        {
            if (!Guid.TryParse(fkValue.StringValue, out var g) || g == Guid.Empty)
                errors.Add($"'{relation.ForeignKey}': must be a valid non-empty GUID.");
            return;
        }

        var navValue = GetFieldValue(payload, relation.PropertyName);
        if (navValue?.StructValue is { } nested)
        {
            ValidateNestedObject(nested, relation.PropertyName, relation.RelatedTypeName, errors);
            return;
        }

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
        // empty collection is valid
    }

    private void ValidateNestedObject(Struct nested, string path, string relatedTypeName, List<string> errors)
    {
        var relatedSchema  = _registry.Get(relatedTypeName);
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
}

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
