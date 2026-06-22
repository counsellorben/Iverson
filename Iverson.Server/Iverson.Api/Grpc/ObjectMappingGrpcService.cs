using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Elasticsearch;
using Iverson.Events;
using Iverson.Sql;
using Iverson.Vector;
using Microsoft.Extensions.Logging;
using EsFieldType      = Iverson.Elasticsearch.EsFieldType;
using SchemaRelKind    = Iverson.Api.Schema.RelationKind;
using ContractsRelKind = Iverson.Client.Contracts.RelationKind;

namespace Iverson.Api.Grpc;

/// <summary>
/// Implements full entity CRUD with server-side relationship resolution.
/// Routing to backing stores (SQL / ES / Qdrant / Kafka) is determined by
/// the server's entity schema — the client is ignorant of this mapping.
/// </summary>
public sealed class ObjectMappingGrpcService(
    IPostgresRepository _sql,
    IElasticsearchService _es,
    IVectorService _vector,
    IEventProducer events,
    SchemaRegistry registry,
    ILogger<ObjectMappingGrpcService> logger)
    : ObjectMappingService.ObjectMappingServiceBase
{
    // ── Schema registration ────────────────────────────────────────────────────

    public override async Task<SchemaResponse> RegisterSchema(
        SchemaRequest request, ServerCallContext context)
    {
        logger.LogInformation("[RegisterSchema] root={Type} dependents={Deps}",
            request.RootType?.TypeName, request.Dependents.Count);

        if (request.RootType is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "root_type is required."));

        // Eligibility: root must have no ManyToOne or ManyToMany relations
        var ineligible = request.RootType.Relations
            .Where(r => r.Kind is ContractsRelKind.ManyToOne or ContractsRelKind.ManyToMany)
            .Select(r => r.PropertyName)
            .ToList();

        if (ineligible.Count > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"'{request.RootType.TypeName}' cannot be registered as a root type — " +
                $"it carries ManyToOne/ManyToMany relations on: {string.Join(", ", ineligible)}. " +
                $"Register the owning entity instead."));

        var registered = new List<string>();

        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            var descriptor = BuildDescriptor(typeDesc);

            await _sql.ApplySchemaAsync(ToTableSchema(descriptor));
            await _es.ApplyMappingAsync(ToIndexSchema(descriptor));

            if (descriptor.VectorFields.Count > 0)
                await _vector.ApplyCollectionAsync(ToCollectionSchema(descriptor));

            if (descriptor.ChunkFields.Count > 0)
                await _vector.ApplyCollectionAsync(ToChunkCollectionSchema(descriptor));

            await registry.RegisterAsync(descriptor);
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
        logger.LogInformation("[Mapping.Get] type={Type} key={Key} depth={Depth}",
            request.TypeName, request.Key, request.Depth);

        // TODO: resolve schema, fan out to backing stores, traverse relations to depth
        return await Task.FromResult(new MappingResponse
        {
            Success = false,
            Error   = $"Schema for '{request.TypeName}' not yet registered on the server.",
            TraceId = request.TraceId
        });
    }

    public override async Task<MappingResponse> Post(
        MappingWriteRequest request, ServerCallContext context)
    {
        logger.LogInformation("[Mapping.Post] type={Type}", request.TypeName);

        // TODO: deserialise payload, write to all annotated stores, emit OnCreate event.
        await events.ProduceAsync($"{request.TypeName}.created", request.TypeName,
            new { type = request.TypeName, traceId = request.TraceId });

        return new MappingResponse
        {
            Success = false,
            Error   = $"Schema for '{request.TypeName}' not yet registered on the server.",
            TraceId = request.TraceId
        };
    }

    public override async Task<MappingResponse> Update(
        MappingWriteRequest request, ServerCallContext context)
    {
        logger.LogInformation("[Mapping.Update] type={Type}", request.TypeName);

        await events.ProduceAsync($"{request.TypeName}.updated", request.TypeName,
            new { type = request.TypeName, traceId = request.TraceId });

        return new MappingResponse
        {
            Success = false,
            Error   = $"Schema for '{request.TypeName}' not yet registered on the server.",
            TraceId = request.TraceId
        };
    }

    public override async Task<MappingDeleteResponse> Delete(
        MappingDeleteRequest request, ServerCallContext context)
    {
        logger.LogInformation("[Mapping.Delete] type={Type} key={Key}", request.TypeName, request.Key);

        await events.ProduceAsync($"{request.TypeName}.deleted", request.Key,
            new { type = request.TypeName, key = request.Key, traceId = request.TraceId });

        return new MappingDeleteResponse
        {
            Success = false,
            Error   = $"Schema for '{request.TypeName}' not yet registered on the server.",
            TraceId = request.TraceId
        };
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static SchemaDescriptor BuildDescriptor(TypeDescriptor typeDesc)
    {
        var tableName = ToSnakeCase(typeDesc.TypeName) + "s";

        var keyProp = typeDesc.Properties.FirstOrDefault(p => p.IsKey)
            ?? throw new InvalidOperationException($"No key property on '{typeDesc.TypeName}'.");

        var scalars = new List<ColumnDescriptor>();
        var fks     = new List<FkDescriptor>();
        var vectors = new List<VectorDescriptor>();
        var chunks  = new List<ChunkDescriptor>();

        foreach (var prop in typeDesc.Properties.Where(p => !p.IsKey))
        {
            var sqlType = ClrTypeToSql(prop.ClrType, prop.IsArray);
            scalars.Add(new ColumnDescriptor(prop.Name, sqlType, prop.IsNullable));

            // Embedding and chunk annotations are additive — the field remains a scalar column
            // in SQL/ES and additionally drives Qdrant vector/chunk ingestion.
            if (prop.IsEmbedding)
                vectors.Add(new VectorDescriptor(prop.Name, prop.VectorDim, prop.ModelId));

            if (prop.IsChunk)
                chunks.Add(new ChunkDescriptor(prop.Name, prop.ChunkMaxTokens, prop.ChunkOverlap, prop.ChunkModelId, prop.ChunkVectorDim));

            if (prop.Name.EndsWith("Id",  StringComparison.OrdinalIgnoreCase) ||
                prop.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))
            {
                var relatedType = typeDesc.Relations
                    .FirstOrDefault(r => r.ForeignKey == prop.Name)?.RelatedType ?? string.Empty;
                fks.Add(new FkDescriptor(prop.Name, relatedType));
            }
        }

        var relations = typeDesc.Relations.Select(r => new RelationEntry(
            r.PropertyName,
            r.Kind switch
            {
                ContractsRelKind.OneToOne  => SchemaRelKind.OneToOne,
                ContractsRelKind.OneToMany => SchemaRelKind.OneToMany,
                ContractsRelKind.ManyToOne => SchemaRelKind.ManyToOne,
                _                          => SchemaRelKind.ManyToMany
            },
            r.RelatedType,
            r.ForeignKey
        )).ToList();

        return new SchemaDescriptor
        {
            TypeName       = typeDesc.TypeName,
            TableName      = tableName,
            IndexName      = tableName,
            CollectionName = (vectors.Count > 0 || chunks.Count > 0) ? tableName : null,
            KeyColumn      = new ColumnDescriptor(keyProp.Name, ClrTypeToSql(keyProp.ClrType, false), false),
            ScalarColumns  = scalars,
            FkColumns      = fks,
            VectorFields   = vectors,
            ChunkFields    = chunks,
            Relations      = relations
        };
    }

    private static TableSchema ToTableSchema(SchemaDescriptor d) => new(
        d.TableName,
        ToColumnSchema(d.KeyColumn),
        d.ScalarColumns.Select(ToColumnSchema).ToList());

    private static ColumnSchema ToColumnSchema(ColumnDescriptor c) =>
        new(c.Name, c.SqlType, c.IsNullable);

    private static IndexSchema ToIndexSchema(SchemaDescriptor d)
    {
        var fields = new List<FieldMapping>
        {
            new(d.KeyColumn.Name, EsFieldType.Keyword)
        };
        foreach (var col in d.ScalarColumns)
            fields.Add(new FieldMapping(col.Name, SqlTypeToEsType(col.SqlType)));

        // Embedding fields are already stored as text scalars above.
        // Add a companion dense_vector field named {property}_vector for kNN retrieval.
        foreach (var v in d.VectorFields)
            fields.Add(new FieldMapping($"{ToSnakeCase(v.PropertyName)}_vector", EsFieldType.DenseVector, v.Dimension));

        return new IndexSchema(d.IndexName, fields);
    }

    private static CollectionSchema ToChunkCollectionSchema(SchemaDescriptor d) => new(
        d.CollectionName! + "_chunks",
        d.ChunkFields.Select(c => new NamedVector($"{ToSnakeCase(c.PropertyName)}_vector", c.Dimension)).ToList(),
        [new PayloadIndex("parent_id", PayloadIndexKind.Keyword)]);

    private static CollectionSchema ToCollectionSchema(SchemaDescriptor d) => new(
        d.CollectionName!,
        d.VectorFields.Select(v => new NamedVector($"{ToSnakeCase(v.PropertyName)}_vector", v.Dimension)).ToList(),
        d.ScalarColumns
            .Select(c => new PayloadIndex(c.Name, SqlTypeToPayloadKind(c.SqlType)))
            .Concat(d.FkColumns.Select(fk => new PayloadIndex(fk.ColumnName, PayloadIndexKind.Keyword)))
            .ToList());

    private static string ClrTypeToSql(ClrType t, bool isArray) => (t, isArray) switch
    {
        (ClrType.ClrGuid,     false) => "UUID",
        (ClrType.ClrGuid,     true)  => "UUID[]",
        (ClrType.ClrString,   _)     => "TEXT",
        (ClrType.ClrInt32,    _)     => "INTEGER",
        (ClrType.ClrInt64,    _)     => "BIGINT",
        (ClrType.ClrFloat,    true)  => "REAL[]",
        (ClrType.ClrFloat,    false) => "REAL",
        (ClrType.ClrDouble,   _)     => "DOUBLE PRECISION",
        (ClrType.ClrBool,     _)     => "BOOLEAN",
        (ClrType.ClrDatetime, _)     => "TIMESTAMPTZ",
        (ClrType.ClrBytes,    _)     => "BYTEA",
        _                            => "TEXT"
    };

    private static EsFieldType SqlTypeToEsType(string sqlType) => sqlType.ToUpperInvariant() switch
    {
        "UUID"             => EsFieldType.Keyword,
        "UUID[]"           => EsFieldType.Keyword,
        "TEXT"             => EsFieldType.Text,
        "INTEGER"          => EsFieldType.Integer,
        "BIGINT"           => EsFieldType.Long,
        "REAL"             => EsFieldType.Float,
        "REAL[]"           => EsFieldType.Float,
        "DOUBLE PRECISION" => EsFieldType.Double,
        "BOOLEAN"          => EsFieldType.Boolean,
        "TIMESTAMPTZ"      => EsFieldType.Date,
        _                  => EsFieldType.Text
    };

    private static PayloadIndexKind SqlTypeToPayloadKind(string sqlType) => sqlType.ToUpperInvariant() switch
    {
        "UUID"             => PayloadIndexKind.Keyword,
        "UUID[]"           => PayloadIndexKind.Keyword,
        "TEXT"             => PayloadIndexKind.Keyword,
        "INTEGER"          => PayloadIndexKind.Integer,
        "BIGINT"           => PayloadIndexKind.Integer,
        "REAL"             => PayloadIndexKind.Float,
        "DOUBLE PRECISION" => PayloadIndexKind.Float,
        "BOOLEAN"          => PayloadIndexKind.Boolean,
        "TIMESTAMPTZ"      => PayloadIndexKind.Datetime,
        _                  => PayloadIndexKind.Keyword
    };

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
