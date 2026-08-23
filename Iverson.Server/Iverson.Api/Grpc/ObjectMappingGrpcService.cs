using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.AspNetCore.Authorization;
using ContractsRelationKind = Iverson.Client.Contracts.RelationKind;
using SchemaRelationKind    = Iverson.Api.Schema.RelationKind;

namespace Iverson.Api.Grpc;

/// <summary>
/// Implements full entity CRUD with server-side relationship resolution.
/// Routing to backing stores (SQL / StarRocks / Qdrant / Kafka) is determined by
/// the server's entity schema — the client is ignorant of this mapping.
/// </summary>
public sealed class ObjectMappingGrpcService(
    IEntityRepository _entities,
    IRecordStoreTransactionRunner _txRunner,
    IOutboxPublisher _outboxPublisher,
    SchemaRegistry _registry,
    IRelationValidator _relationValidator,
    IEntityKeyAccessor _keyAccessor,
    IOutboxWriter _outboxWriter,
    ILogger<ObjectMappingGrpcService> _logger,
    IActingUserAccessor _actingUserAccessor,
    IRowFieldAuthorizationEvaluator _authEvaluator,
    IEntityRelationResolver _relationResolver,
    ISchemaRegistrationOrchestrator _schemaRegistration,
    AuditLog _auditLog)
    : ObjectMappingService.ObjectMappingServiceBase
{
    // ── Schema registration ────────────────────────────────────────────────────

    [Authorize(Policy = "SchemaAdmin")]
    public override async Task<SchemaResponse> RegisterSchema(
        SchemaRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[RegisterSchema] root={Type} dependents={Deps}",
            request.RootType?.TypeName?.SanitizeForLog(), request.Dependents.Count);

        if (request.RootType is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "root_type is required."));

        var registered = await _schemaRegistration.RegisterAsync(request, context.CancellationToken);

        _auditLog.AdminOperation(context.GetHttpContext().User, "RegisterSchema", request.RootType.TypeName);

        return new SchemaResponse
        {
            Success    = true,
            TraceId    = request.TraceId,
            Registered = { registered }
        };
    }

    // No [Authorize] here — GetSchema is discovery, meant to be reachable by any authenticated
    // caller. It inherits the ambient RequireAuthenticatedUser() fallback policy.
    public override Task<GetSchemaResponse> GetSchema(
        GetSchemaRequest request,
        ServerCallContext context)
    {
        // Two-pass: pass one decides which types survive (row-level denial, then an empty
        // authorized field set), pass two emits relations, dropping any whose related_type
        // did not survive pass one — that cross-type check can't be made until every type has
        // been evaluated.
        var survivors = new List<(SchemaDescriptor Schema, List<SchemaField> Fields, AuthorizationDecision Decision)>();
        // OrdinalIgnoreCase to match SchemaRegistry's own keying (SchemaRegistry.cs) and every
        // other RelationDescriptor.RelatedTypeName lookup (EntityRelationResolver), so a relation
        // declaring a differently-cased related type is not silently dropped from the catalog.
        var survivingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var schema in _registry.All.Values)
        {
            var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
            if (decision.Denied)
                continue;

            // ScalarColumns position: EXCLUDE __TenantId. This catalog is client-facing — it is the
            // one RPC whose whole job is telling a client what it may address. The tenant column is
            // server-owned and never appears on the wire, so publishing it here would undo that.
            IEnumerable<ColumnDescriptor> candidates = new[] { schema.KeyColumn }
                .Concat(schema.ScalarColumns.Where(c => !SchemaDescriptor.IsTenantColumn(c.Name)));
            if (decision.AllowedFields is not null)
                candidates = candidates.Where(c => decision.AllowedFields.Contains(c.Name));

            var fields = candidates
                .Select(c => ProjectField(c, schema))
                .Where(f => f is not null)
                .Select(f => f!)
                .ToList();
            if (fields.Count == 0)
                continue; // Fail-closed guard; the real evaluator always keeps the key column.

            survivors.Add((schema, fields, decision));
            survivingNames.Add(schema.TypeName);
        }

        var response = new GetSchemaResponse();
        foreach (var (schema, fields, decision) in survivors)
        {
            var schemaType = new SchemaType
            {
                Name        = schema.TypeName,
                Description = schema.Description ?? string.Empty
            };
            schemaType.Fields.AddRange(fields);
            schemaType.Relations.AddRange(
                schema.Relations
                    // Two conditions. The related type must have survived pass one, and — only
                    // where the FK column is local to this type — the FK must itself be readable.
                    .Where(r => survivingNames.Contains(r.RelatedTypeName)
                             && ForeignKeyIsReadable(r, decision))
                    .Select(r => new SchemaRelation
                    {
                        PropertyName = r.PropertyName,
                        Kind = r.Kind switch
                        {
                            SchemaRelationKind.OneToOne   => ContractsRelationKind.OneToOne,
                            SchemaRelationKind.OneToMany  => ContractsRelationKind.OneToMany,
                            SchemaRelationKind.ManyToOne  => ContractsRelationKind.ManyToOne,
                            SchemaRelationKind.ManyToMany => ContractsRelationKind.ManyToMany,
                            _ => throw new ArgumentOutOfRangeException(nameof(r.Kind), r.Kind,
                                $"Unhandled {nameof(SchemaRelationKind)} value — add a case above.")
                        },
                        RelatedType = r.RelatedTypeName,
                        ForeignKey  = r.ForeignKey
                    }));
            response.Types_.Add(schemaType);
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// Whether <paramref name="relation"/>'s foreign key may be disclosed to this caller.
    /// <para>
    /// Every FK property is also an ordinary scalar column, so a <c>FieldPermission</c> that removed
    /// it from <c>fields</c> would otherwise still leak its exact name as <c>foreign_key</c>. But
    /// that reasoning only holds where the FK column lives on the <em>declaring</em> type, which
    /// depends on the relation kind (see <c>EntityRelationResolver</c>):
    /// </para>
    /// <list type="bullet">
    /// <item><c>OneToOne</c> / <c>ManyToOne</c> — reads <c>ForeignKey</c> off the declaring entity.</item>
    /// <item><c>ManyToMany</c> — reads <c>ForeignKey</c> as a list off the declaring entity.</item>
    /// <item><c>OneToMany</c> — <c>ForeignKey</c> is a column on the <em>related</em> type's table,
    /// matched against this type's key. It is not a member of this schema at all.</item>
    /// </list>
    /// <para>
    /// <c>AllowedFields</c> only ever holds the declaring schema's own members
    /// (<c>RowFieldAuthorizationEvaluator</c>), so applying the check to <c>OneToMany</c> would drop
    /// every such relation from the catalog whenever any <c>FieldPermission</c> is active — the
    /// check can never be satisfied. Hence the kind gate.
    /// </para>
    /// </summary>
    private static bool ForeignKeyIsReadable(
        Iverson.Api.Schema.RelationDescriptor relation, AuthorizationDecision decision)
    {
        if (decision.AllowedFields is null)
            return true;

        return relation.Kind switch
        {
            SchemaRelationKind.OneToMany => true, // FK belongs to the related type, not this one.
            SchemaRelationKind.OneToOne or
            SchemaRelationKind.ManyToOne or
            SchemaRelationKind.ManyToMany => decision.AllowedFields.Contains(relation.ForeignKey),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation.Kind,
                $"Unhandled {nameof(SchemaRelationKind)} value — add a case above.")
        };
    }

    /// <summary>
    /// Projects one persisted column into its wire form, or <c>null</c> when the column's SQL type
    /// is not known to this build (a descriptor persisted by an older build). Skipping the column
    /// keeps the rest of the catalog available instead of failing the whole RPC.
    /// <para>
    /// The <b>key column is exempt</b>: it is not optional. Emitting a type with no <c>is_key</c>
    /// field would hand the caller a schema it cannot address a <c>Get</c> with, and the empty-field
    /// guard in pass one assumes the key always survives. An unmapped key type is unrecoverable for
    /// that type, so it throws and the type is failed loudly rather than silently degraded.
    /// </para>
    /// </summary>
    private SchemaField? ProjectField(ColumnDescriptor col, SchemaDescriptor schema)
    {
        if (!SchemaBuilder.TrySqlTypeToClr(col.SqlType, out var mapping))
        {
            if (col.Name == schema.KeyColumn.Name)
                throw new ArgumentOutOfRangeException(nameof(col), col.SqlType,
                    $"Key column '{schema.TypeName}.{col.Name}' has unmapped SQL type " +
                    $"'{col.SqlType}'; the catalog cannot describe a type without its key.");

            _logger.LogWarning(
                "[GetSchema] Skipping column {Type}.{Column}: unmapped SQL type '{SqlType}'.",
                schema.TypeName.SanitizeForLog(), col.Name.SanitizeForLog(), col.SqlType.SanitizeForLog());
            return null;
        }

        var (clrType, isArray) = mapping;
        var searchKeyOrder = schema.SearchKeyColumns.IndexOf(col.Name);

        var field = new SchemaField
        {
            Name           = col.Name,
            Description    = schema.FieldDescriptions.TryGetValue(col.Name, out var desc) ? desc : string.Empty,
            ClrType        = clrType,
            IsArray        = isArray,
            IsKey          = col.Name == schema.KeyColumn.Name,
            IsNullable     = col.IsNullable,
            IsMetadata     = schema.MetadataColumns.Contains(col.Name),
            IsSearchKey    = searchKeyOrder >= 0,
            SearchKeyOrder = searchKeyOrder >= 0 ? searchKeyOrder : 0,
            IsEmbedding    = schema.VectorFields.Any(v => v.PropertyName == col.Name),
            IsChunk        = schema.ChunkFields.Any(c => c.PropertyName == col.Name)
        };

        field.Enrichment.AddRange(
            schema.EnrichmentTargets
                .Where(t => t.ColumnName == col.Name)
                .Select(t => t.Kind switch
                {
                    EnrichmentKind.Summary   => SchemaEnrichmentKind.EnrichmentSummary,
                    EnrichmentKind.Keywords  => SchemaEnrichmentKind.EnrichmentKeywords,
                    EnrichmentKind.Extracted => SchemaEnrichmentKind.EnrichmentExtracted,
                    _ => throw new ArgumentOutOfRangeException(nameof(t.Kind), t.Kind,
                        $"Unhandled {nameof(EnrichmentKind)} value — add a case above.")
                }));

        return field;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public override async Task<MappingResponse> Get(
        MappingGetRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Get] type={Type} key={Key} depth={Depth}",
            request.TypeName.SanitizeForLog(), request.Key, request.Depth);

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            tenantScoped: schema.TenantColumn is not null,
            tenantId: _actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value);
        if (rowJson is null)
            return new MappingResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var entityStruct = JsonParser.Default.Parse<Struct>(rowJson);

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
        var ownerMismatch  = decision.OwnershipRequired &&
            StructFieldAccess.GetFieldString(entityStruct, decision.OwnerFieldName!) != decision.OwnerValue;
        var tenantMismatch = decision.TenantColumn is not null &&
            StructFieldAccess.GetFieldString(entityStruct, decision.TenantColumn) != decision.TenantValue;
        if (decision.Denied || ownerMismatch || tenantMismatch)
        {
            _auditLog.Denied(_actingUserAccessor.ActingUser, "Read", request.TypeName, request.Key,
                decision.Denied ? "AccessDenied" : ownerMismatch ? "OwnerMismatch" : "TenantMismatch");
            return new MappingResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };
        }

        AuthorizationFieldMasking.MaskDisallowedFields(entityStruct, decision.AllowedFields);

        if (request.Depth > 0)
            await _relationResolver.ResolveRelationsAsync(
                entityStruct,
                schema,
                request.Depth,
                _actingUserAccessor.ActingUser,
                context.CancellationToken);

        return new MappingResponse { Success = true, Data = entityStruct, TraceId = request.TraceId };
    }

    public override async Task<MappingResponse> Post(
        MappingWriteRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Post] type={Type}", request.TypeName.SanitizeForLog());

        var schema = RequireSchema(request.TypeName);

        AuthorizationFieldMasking.EnforceWriteAuthorization(
            _authEvaluator, _actingUserAccessor.ActingUser, schema, request.Payload,
            AuthorizationAction.Write, "Not authorized to create this entity.", existingRowJson: null, _auditLog);

        _relationValidator.ValidateAndNormalizeRelations(request.Payload, schema);

        var key = _keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
            SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
            tenantId: decision.TenantValue);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        await _outboxPublisher.PublishAsync(EntityEventType.Created, request.TypeName, key, payloadJson,
            request.TraceId, targetStores, outboxRowId, "Mapping.Post");

        // Strip the server-owned tenant column from the Struct that becomes MappingResponse.Data.
        // EnforceWriteAuthorization force-set it INTO this very object (SetAuthoritativeField ->
        // StructFieldAccess.SetField mutates in place), and `Data = request.Payload` below returns
        // that same object — so without this the column goes back to the caller on every write.
        //
        // AFTER SerializePayload, deliberately. payloadJson is what OutboxPublisher puts on Kafka,
        // and it is the only source of the tenant value for the StarRocks projection
        // (EngagementRepository.UpsertAsync) and the Qdrant point payload
        // (IntelligenceStoreConsumer.BuildObjectPointPayload). Stripping before serialization
        // would leave the StarRocks row's tenant column NULL — StarRocks' Primary Key model
        // treats a partial INSERT as a full-row replace — and every subsequent StarRocks read for
        // that tenant would return nothing. OutboxWriter remains the sole *injector* for the
        // Postgres write; this is only a response-shaping strip.
        AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingResponse> Update(
        MappingWriteRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Update] type={Type}", request.TypeName.SanitizeForLog());

        var schema = RequireSchema(request.TypeName);

        var key = _keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Update requires a non-empty '{schema.KeyColumn.Name}' in the payload."));

        var existingRowJson = await FetchByKeyAsync(schema, key);
        AuthorizationFieldMasking.EnforceWriteAuthorization(
            _authEvaluator,
            _actingUserAccessor.ActingUser,
            schema,
            request.Payload,
            AuthorizationAction.Write,
            "Not authorized to update this entity.",
            existingRowJson,
            _auditLog);

        _relationValidator.ValidateAndNormalizeRelations(request.Payload, schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
            SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
            tenantId: decision.TenantValue);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        await _outboxPublisher.PublishAsync(
            EntityEventType.Updated,
            request.TypeName,
            key,
            payloadJson,
            request.TraceId,
            targetStores,
            outboxRowId,
            "Mapping.Update",
            priorPayloadJson: existingRowJson);

        // Strip the server-owned tenant column from the Struct that becomes MappingResponse.Data.
        // EnforceWriteAuthorization force-set it INTO this very object (SetAuthoritativeField ->
        // StructFieldAccess.SetField mutates in place), and `Data = request.Payload` below returns
        // that same object — so without this the column goes back to the caller on every write.
        //
        // AFTER SerializePayload, deliberately. payloadJson is what OutboxPublisher puts on Kafka,
        // and it is the only source of the tenant value for the StarRocks projection
        // (EngagementRepository.UpsertAsync) and the Qdrant point payload
        // (IntelligenceStoreConsumer.BuildObjectPointPayload). Stripping before serialization
        // would leave the StarRocks row's tenant column NULL — StarRocks' Primary Key model
        // treats a partial INSERT as a full-row replace — and every subsequent StarRocks read for
        // that tenant would return nothing. OutboxWriter remains the sole *injector* for the
        // Postgres write; this is only a response-shaping strip.
        AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingDeleteResponse> Delete(
        MappingDeleteRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Delete] type={Type} key={Key}", request.TypeName.SanitizeForLog(), request.Key);

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            tenantScoped: schema.TenantColumn is not null,
            tenantId: _actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value);
        if (rowJson is null)
            return new MappingDeleteResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var decision  = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Delete);
        var rowStruct = JsonParser.Default.Parse<Struct>(rowJson);
        var ownerMismatch  = decision.OwnershipRequired &&
            StructFieldAccess.GetFieldString(rowStruct, decision.OwnerFieldName!) != decision.OwnerValue;
        var tenantMismatch = decision.TenantColumn is not null &&
            StructFieldAccess.GetFieldString(rowStruct, decision.TenantColumn) != decision.TenantValue;
        if (decision.Denied || ownerMismatch || tenantMismatch)
        {
            _auditLog.Denied(
                _actingUserAccessor.ActingUser,
                "Delete",
                request.TypeName,
                request.Key,
                decision.Denied
                    ? "AccessDenied"
                    : ownerMismatch
                        ? "OwnerMismatch"
                        : "TenantMismatch");
            return new MappingDeleteResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };
        }

        var targetStores = StoreTargeting.DetermineTargetStores(schema);
        var outboxRowId  = Guid.CreateVersion7();

        await _txRunner.ExecuteInTransactionAsync(async tx =>
        {
            await _entities.DeleteAsync(
                tx,
                SchemaBuilder.ToTableSchema(schema),
                request.Key,
                tenantScoped: decision.TenantColumn is not null,
                tenantId: decision.TenantValue);

            await _outboxWriter.EnqueueDeleteOutboxRowAsync(
                tx,
                outboxRowId,
                request.TypeName,
                request.Key,
                rowJson);
        });

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // delete-outbox row committed above, in the same transaction as the entity delete),
        // so a failure here is not data loss — the existing ReconciliationQueueWorker (which
        // now polls unconditionally-inserted outbox rows, not just failure-recorded ones —
        // see Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll and replay it from the stored pre-delete snapshot. This just keeps the
        // common case's projection latency low.
        await _outboxPublisher.PublishAsync(
            EntityEventType.Deleted,
            request.TypeName,
            request.Key,
            rowJson,
            request.TraceId,
            targetStores,
            outboxRowId,
            "Mapping.Delete");

        return new MappingDeleteResponse { Success = true, TraceId = request.TraceId };
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        _registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private Task<string?> FetchByKeyAsync(
        SchemaDescriptor schema, string key, bool tenantScoped = false, string? tenantId = null) =>
        _entities.FetchByKeyAsync(
            SchemaBuilder.ToTableSchema(schema),
            key,
            tenantScoped,
            tenantId);
}
