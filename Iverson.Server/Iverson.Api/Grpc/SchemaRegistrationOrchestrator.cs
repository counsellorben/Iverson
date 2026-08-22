using System.Text.RegularExpressions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Grpc.Core;

namespace Iverson.Api.Grpc;

public interface ISchemaRegistrationOrchestrator
{
    Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct);
}

public sealed class SchemaRegistrationOrchestrator(
    IRecordStoreSchemaManager schemaManager,
    IEmbeddingService embedding,
    SchemaRegistry registry,
    IDocumentRerenderQueueRepository rerenderQueue,
    ILogger<SchemaRegistrationOrchestrator> logger)
    : ISchemaRegistrationOrchestrator
{
    // TypeName/property names are string-interpolated unescaped into CREATE TABLE/ALTER TABLE
    // DDL by PostgresSchemaManager/StarRocksSchemaManager after only a case transformation
    // (NamingExtensions.ToSnakeCase, which does not escape or reject anything). Validate at
    // the source — every descriptor that reaches SchemaBuilder.BuildDescriptor must already be
    // a safe DDL identifier. No underscores are permitted in the input because ToSnakeCase
    // inserts its own; this pattern also naturally rejects an empty string.
    private static readonly Regex IdentifierPattern = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<string>> RegisterAsync(SchemaRequest request, CancellationToken ct)
    {
        // Phase 1: build + per-type validate. No DDL, no registry writes — a root's document
        // template can reference a dependent that hasn't been built yet if this were a single
        // pass, and applying/registering before every type in the request is known-good would
        // persist an invalid template.
        var descriptors = new List<SchemaDescriptor>();
        var batchDescriptors = new Dictionary<string, SchemaDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            ValidateIdentifier(typeDesc.TypeName, "type_name");
            foreach (var property in typeDesc.Properties)
                ValidateIdentifier(property.Name, $"property name on type '{typeDesc.TypeName}'");

            try
            {
                await embedding.EnsureInitializedAsync(ct);
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.Unavailable,
                    $"Embedding service is unavailable, so schema registration cannot determine the vector "
                    + $"dimension. Check that Ollama is reachable and retry. ({ex.Message})"));
            }

            SchemaDescriptor descriptor;
            try
            {
                descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);
            }
            catch (DocumentTemplateParseException ex)
            {
                // BuildDescriptor is where DocumentTemplateParser.Parse actually runs (T1); this
                // is the only place that exception can surface. RegisterAsync has no other catch
                // covering it, and the sole registered gRPC interceptor resolves acting-user
                // identity without mapping exceptions, so an uncaught parse failure would reach
                // the client as StatusCode.Unknown instead of InvalidArgument.
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Document template on '{typeDesc.TypeName}' is invalid: {ex.Message} (placeholder: '{ex.Placeholder}')"));
            }

            ValidateEnrichmentTargets(typeDesc, descriptor);

            var ownerField = descriptor.Authorization?.OwnerField;
            if (!string.IsNullOrEmpty(ownerField))
                ValidateFieldReference(descriptor, ownerField, "owner_field");

            // tenant_field is MANDATORY (unlike owner_field): every schema must declare a
            // platform-enforced tenant boundary, independent of whatever AuthorizationRules
            // it configures.
            if (string.IsNullOrEmpty(descriptor.TenantColumn))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"tenant_field is required on '{descriptor.TypeName}'."));
            }
            ValidateFieldReference(descriptor, descriptor.TenantColumn, "tenant_field");

            // The key column is compared against a uuid parameter in every EntityRepository
            // predicate (FetchByKey/FetchMany/FetchByColumn/Delete/Update). A non-UUID key
            // registers cleanly and then fails every read with Postgres 42883.
            if (!string.Equals(descriptor.KeyColumn.SqlType, "UUID", StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Key property '{descriptor.KeyColumn.Name}' on '{descriptor.TypeName}' has SQL type " +
                    $"'{descriptor.KeyColumn.SqlType}', but a key column must be UUID. Declare the key as a " +
                    $"GUID/UUID-typed property in your client model (.NET: Guid; Java: UUID; Python: uuid.UUID; " +
                    $"Go: add the `iverson_guid:\"true\"` struct tag; TypeScript: add the @IversonGuid() decorator)."));
            }

            // Membership only — NOT ValidateFieldReference, which additionally requires a string-valued
            // SqlType for Qdrant filtering and would reject a ManyToMany's UUID[] foreign key.
            // OneToMany is exempt: its foreign key is a column on the RELATED type's row.
            foreach (var relation in descriptor.Relations.Where(r => r.Kind != Schema.RelationKind.OneToMany))
            {
                var column = descriptor.ScalarColumns.FirstOrDefault(c =>
                    string.Equals(c.Name, relation.ForeignKey, StringComparison.OrdinalIgnoreCase));

                if (column is null)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
                        $"declares foreign key '{relation.ForeignKey}', which is not a declared property."));
                }

                // ManyToMany's foreign key is a list of ids (UUID[]); the others are a single id (UUID).
                // Only the OneToMany reverse lookup compares an FK column in SQL, so the UUID[] arm is a
                // consistency rule — but a TEXT[] column would still be wrong by construction.
                var requiredSqlType = relation.Kind == Schema.RelationKind.ManyToMany ? "UUID[]" : "UUID";
                if (!string.Equals(column.SqlType, requiredSqlType, StringComparison.Ordinal))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
                        $"declares foreign key '{relation.ForeignKey}' with SQL type '{column.SqlType}', " +
                        $"but a {relation.Kind} foreign key must be {requiredSqlType}. Declare it as a " +
                        $"GUID/UUID-typed property{(relation.Kind == Schema.RelationKind.ManyToMany ? " array" : "")} " +
                        $"(.NET: Guid; Java: UUID; Python: uuid.UUID; Go: add the `iverson_guid:\"true\"` struct tag; " +
                        $"TypeScript: add the @IversonGuid() decorator)."));
                }

                // The foreign key must be named after the RELATED type, e.g. "AuthorId" for a
                // relation to Author — not whatever the caller happened to call the property.
                // OneToMany is exempt (see the loop filter above): its foreign key is named after
                // THIS type and lives on the related type's row, so this rule does not apply to it.
                var requiredSuffix = relation.Kind == Schema.RelationKind.ManyToMany ? "Ids" : "Id";
                var requiredForeignKey = relation.RelatedTypeName + requiredSuffix;
                if (!string.Equals(relation.ForeignKey, requiredForeignKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
                        $"declares foreign key '{relation.ForeignKey}', but a {relation.Kind} foreign key " +
                        $"referencing '{relation.RelatedTypeName}' must be named '{requiredForeignKey}'."));
                }
            }

            // The navigation-property name must be distinct from the foreign-key name, for every
            // relation kind including OneToMany (per the IVC-REL-003 ruling) — unlike the naming
            // check above, this pass is NOT filtered by kind. When the two names collide, there is
            // no separate nav property: writes and reads of the "nav property" silently alias the
            // foreign key, and ResolveManyToManyAsync-style hydration overwrites it outright.
            foreach (var relation in descriptor.Relations)
            {
                if (Schema.RelationCollisionCheck.IsCollision(relation))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Relation '{relation.PropertyName}' ({relation.Kind}) on '{descriptor.TypeName}' " +
                        $"has a navigation-property name identical to its foreign key '{relation.ForeignKey}'. " +
                        $"The navigation-property name must be distinct from the foreign key."));
                }
            }

            descriptors.Add(descriptor);
            batchDescriptors[descriptor.TypeName] = descriptor;
        }

        // Phase 2: cross-type validate. Every type in this request is now built, so a root's
        // {Rel.Prop} reference into a dependent (in either declaration order) can resolve.
        // Resolution also reaches types already registered from an earlier call, not just this
        // request's batch — effectiveDescriptors is the registry's current view with this
        // request's freshly-built descriptors overlaid on top.
        var effectiveDescriptors = new Dictionary<string, SchemaDescriptor>(registry.All, StringComparer.OrdinalIgnoreCase);
        foreach (var (typeName, descriptor) in batchDescriptors)
            effectiveDescriptors[typeName] = descriptor;

        // This request's own descriptors: a validation failure here is this request's own bad
        // submission.
        foreach (var descriptor in descriptors)
            ValidateDocumentTemplate(descriptor, effectiveDescriptors, StatusCode.InvalidArgument);

        // Every OTHER already-registered type that carries a document template: this request
        // didn't touch it directly, but it may reference (via a one-hop/block relation) a type
        // this request just changed. If its template no longer resolves against the effective
        // view, this request is breaking an established contract — FailedPrecondition, the same
        // status SchemaDriftException already uses below for breaking an existing schema.
        foreach (var (typeName, descriptor) in effectiveDescriptors)
        {
            if (batchDescriptors.ContainsKey(typeName) || descriptor.DocumentTemplate is null)
                continue;

            ValidateDocumentTemplate(descriptor, effectiveDescriptors, StatusCode.FailedPrecondition);
        }

        // Capture which of this request's types have a changed document template, while
        // registry.Get still returns the PRIOR descriptor (registry.RegisterAsync in phase 3
        // below overwrites it). Compared on DocumentTemplateSource — the raw template string —
        // not the parsed DocumentTemplate: record equality on the parsed model's segment list
        // uses EqualityComparer<T>.Default, which for a collection is reference equality, so two
        // structurally identical templates would never compare equal and every registration
        // would look changed. A null prior source (no template previously) counts as changed
        // when the new source is non-null — a newly added template needs the same backfill as
        // an edited one. An unchanged template must NOT be recorded: re-registering an unchanged
        // schema is routine (every service restart re-runs registration), and enqueuing on every
        // such call would put a type-level row in the queue perpetually.
        var changedTemplateTypes = new List<string>();
        foreach (var descriptor in descriptors)
        {
            var priorSource = registry.Get(descriptor.TypeName)?.DocumentTemplateSource;
            if (string.Equals(priorSource, descriptor.DocumentTemplateSource, StringComparison.Ordinal))
                continue;

            // A template REMOVAL (prior non-null, new null) must not enqueue a type-level
            // backfill: with the template gone, SchemaBuilder no longer emits the synthetic
            // "Document" chunk field, so ChunkFields has no Document entry and the orphan-delete
            // pass in IntelligenceStoreConsumer (which iterates ChunkFields) can never clean up
            // the old document_vector chunk points — the backfill would just re-ingest every
            // entity of the type for nothing. Deleting those points here is DELIBERATELY
            // DEFERRED, not impossible: it would iterate every tenant's per-tenant chunk
            // collection (tenant is baked into the collection name, not a payload filter — see
            // IntelligenceTenantScope.ResolveCollectionName), which needs two collaborators this
            // orchestrator does not yet take — ITenantRepository.ListAsync for the tenant list
            // and IVectorWriteService for the delete. Both exist and are already DI-registered;
            // adding them here is a scope decision, not a blocked one. Log a clear warning
            // instead so the gap is visible, and skip the pointless enqueue either way.
            if (priorSource is not null && descriptor.DocumentTemplateSource is null)
            {
                logger.LogWarning(
                    "Document template removed for type '{TypeName}'; stale 'Document' chunk " +
                    "points in every tenant's {{collection}}_chunks collection are NOT " +
                    "automatically deleted and must be cleaned up manually.",
                    descriptor.TypeName);
                continue;
            }

            changedTemplateTypes.Add(descriptor.TypeName);
        }

        // Phase 3: apply + register. Only reached once every type in the request has passed
        // every validation above — an invalid template never applies DDL or persists.
        var registered = new List<string>();
        foreach (var descriptor in descriptors)
        {
            try
            {
                await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor), SchemaDriftPolicy.Throw);
            }
            catch (SchemaDriftException ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
            }

            await registry.RegisterAsync(descriptor);
            registered.Add(descriptor.TypeName);
        }

        // A changed template invalidates every document of that type, because the rendered
        // text is derived data with no stored copy — a type-level row is how "re-render
        // everything of this type" is represented before the key set is known (T8 expands it
        // by paging every entity of the type).
        foreach (var typeName in changedTemplateTypes)
            await rerenderQueue.EnqueueTypeAsync(typeName);

        return registered;
    }

    // Sits beside ValidateFieldReference/ValidateEnrichmentTargets. The parser (T1) knows
    // nothing about schemas — this is where "does this property/relation actually exist" is
    // enforced. allDescriptors is the effective view for this call (registry ∪ this request's
    // batch); a one-hop/block placeholder resolves its relation's target against it. statusCode
    // is decided by the caller: InvalidArgument when descriptor itself is part of this request's
    // own submission, FailedPrecondition when descriptor is an unrelated, already-registered
    // type whose template broke because this request changed something it depends on.
    private static void ValidateDocumentTemplate(
        SchemaDescriptor descriptor,
        IReadOnlyDictionary<string, SchemaDescriptor> allDescriptors,
        StatusCode statusCode)
    {
        if (descriptor.DocumentTemplate is null)
            return;

        // "Document", "document", and "DOCUMENT" all derive "document_vector".
        var duplicate = descriptor.ChunkFields
            .GroupBy(c => c.PropertyName.ToSnakeCase(), StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new RpcException(new Status(statusCode,
                $"'{descriptor.TypeName}' declares chunk fields " +
                $"{string.Join(", ", duplicate.Select(c => $"'{c.PropertyName}'"))} that all derive the same " +
                $"Qdrant vector name '{duplicate.Key}_vector'."));
        }

        // The companion rule needs no code: RowFieldAuthorizationEvaluator's allFields already
        // concatenates ChunkFields property names, so "Document" lands in AllowedFields by
        // construction once a FieldPermission can never exclude it. Reject only the exclusion.
        if (descriptor.Authorization?.FieldPermissions.Any(fp =>
                string.Equals(fp.FieldName, "Document", StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new RpcException(new Status(statusCode,
                $"'{descriptor.TypeName}' declares a FieldPermission naming 'Document', which is reserved for " +
                "the synthetic document chunk field and can never be excluded from a caller's AllowedFields."));
        }

        foreach (var segment in descriptor.DocumentTemplate.Segments)
            ValidateDocumentSegment(segment, descriptor, allDescriptors, statusCode);
    }

    private static void ValidateDocumentSegment(
        DocumentSegment segment,
        SchemaDescriptor declaring,
        IReadOnlyDictionary<string, SchemaDescriptor> allDescriptors,
        StatusCode statusCode)
    {
        switch (segment.Kind)
        {
            case DocumentSegmentKind.Literal:
                break;

            case DocumentSegmentKind.Scalar:
                RequireScalarProperty(declaring, segment.PropertyName!, statusCode);
                break;

            case DocumentSegmentKind.OneHop:
            {
                var relation = RequireRelation(declaring, segment.RelationName!, statusCode);
                if (relation.Kind is Schema.RelationKind.OneToMany or Schema.RelationKind.ManyToMany)
                {
                    throw new RpcException(new Status(statusCode,
                        $"Document template on '{declaring.TypeName}' uses '{{{segment.RelationName}.{segment.PropertyName}}}', " +
                        $"but relation '{segment.RelationName}' ({relation.Kind}) is a collection relation; " +
                        "one-hop placeholders require a single-valued relation."));
                }

                var target = RequireTargetDescriptor(declaring, relation, allDescriptors, statusCode);
                RequireScalarProperty(target, segment.PropertyName!, statusCode);
                break;
            }

            case DocumentSegmentKind.Block:
            {
                var relation = RequireRelation(declaring, segment.RelationName!, statusCode);
                if (relation.Kind is Schema.RelationKind.OneToOne or Schema.RelationKind.ManyToOne)
                {
                    throw new RpcException(new Status(statusCode,
                        $"Document template on '{declaring.TypeName}' uses '{{#{segment.RelationName}}}', " +
                        $"but relation '{segment.RelationName}' ({relation.Kind}) is a single-valued relation; " +
                        "block sections require a collection relation."));
                }

                var target = RequireTargetDescriptor(declaring, relation, allDescriptors, statusCode);
                foreach (var inner in segment.Inner ?? [])
                    if (inner.Kind == DocumentSegmentKind.Scalar)
                        RequireScalarProperty(target, inner.PropertyName!, statusCode);
                break;
            }
        }
    }

    // Shared by top-level {Prop} (context = declaring type) and one-hop/block-inner {Prop}
    // (context = the relation's target type).
    private static void RequireScalarProperty(SchemaDescriptor context, string propertyName, StatusCode statusCode)
    {
        // ScalarColumns position: EXCLUDE __TenantId. A template referencing {__TenantId} would
        // render the server-owned tenant value into the chunk text that SearchChunks returns
        // verbatim, putting the value back on the wire and defeating the whole "server owns the
        // tenant column, it never appears on the wire" decision. Excluded at the lookup rather than
        // special-cased below so the rejection is the SAME "not a declared scalar property" error
        // any unknown name gets — the reserved name is not even distinguishable from a typo.
        var column = context.ScalarColumns.FirstOrDefault(c =>
            !SchemaDescriptor.IsTenantColumn(c.Name) &&
            string.Equals(c.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            throw new RpcException(new Status(statusCode,
                $"Document template references property '{propertyName}' on '{context.TypeName}', which is not " +
                "a declared scalar property."));
        }

        if (context.Authorization?.FieldPermissions.Any(fp =>
                string.Equals(fp.FieldName, propertyName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new RpcException(new Status(statusCode,
                $"Document template references property '{propertyName}' on '{context.TypeName}', which " +
                "carries a FieldPermission; a document template cannot selectively exclude fields per caller."));
        }
    }

    private static Schema.RelationDescriptor RequireRelation(SchemaDescriptor declaring, string relationName, StatusCode statusCode)
    {
        var relation = declaring.Relations.FirstOrDefault(r =>
            string.Equals(r.PropertyName, relationName, StringComparison.OrdinalIgnoreCase));
        if (relation is null)
        {
            throw new RpcException(new Status(statusCode,
                $"Document template on '{declaring.TypeName}' references relation '{relationName}', which is " +
                "not a declared relation."));
        }

        return relation;
    }

    private static SchemaDescriptor RequireTargetDescriptor(
        SchemaDescriptor declaring,
        Schema.RelationDescriptor relation,
        IReadOnlyDictionary<string, SchemaDescriptor> allDescriptors,
        StatusCode statusCode)
    {
        if (!allDescriptors.TryGetValue(relation.RelatedTypeName, out var target))
        {
            throw new RpcException(new Status(statusCode,
                $"Document template on '{declaring.TypeName}' references relation '{relation.PropertyName}', " +
                $"whose related type '{relation.RelatedTypeName}' is not registered."));
        }

        return target;
    }

    // Shared by owner_field (optional) and tenant_field (mandatory) — both name a scalar
    // property that must resolve to a real column, be string-valued (Qdrant filtering requires
    // it), and not collide with a reserved chunk-payload key.
    private static void ValidateFieldReference(
        SchemaDescriptor descriptor,
        string fieldName,
        string fieldLabel)
    {
        if (!descriptor.ScalarColumns.Any(c => string.Equals(c.Name, fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"{fieldLabel} '{fieldName}' on '{descriptor.TypeName}' does not match any declared scalar property."));
        }

        var column = descriptor.ScalarColumns.First(c =>
            string.Equals(c.Name, fieldName, StringComparison.OrdinalIgnoreCase));

        // Allow-list, not a reject-list: IntelligenceStoreConsumer.ExtractTypedValue's default branch
        // only produces a clean scalar string for these 4 SqlTypes. Every other SqlType — including
        // the array variants UUID[]/REAL[] that SchemaBuilder.ArrayTypeOverrides can also produce for
        // a scalar column — falls through to JsonElement.ToString(), which for a non-string JSON value
        // (a number, bool, or array) produces something that can never equal a real caller's identity
        // value, silently excluding every caller (including the legitimate owner/tenant) from every result.
        var stringValuedSqlTypes = new[] { "TEXT", "UUID", "BYTEA", "TIMESTAMPTZ" };
        if (!stringValuedSqlTypes.Contains(column.SqlType.ToUpperInvariant()))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{fieldLabel} '{fieldName}' on '{descriptor.TypeName}' has SqlType '{column.SqlType}', " +
                $"which is not string-valued; Qdrant filtering requires a string-valued {fieldLabel}."));
        }

        if (descriptor.ChunkFields.Count > 0)
        {
            var reservedChunkKeys = new[] { "text", "parent_id", "field", "chunk_index" };
            var camelField = fieldName.ToCamelCase();
            if (reservedChunkKeys.Contains(camelField))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"{fieldLabel} '{fieldName}' on '{descriptor.TypeName}' camelCases to '{camelField}', " +
                    $"which collides with a reserved chunk-payload key ({string.Join(", ", reservedChunkKeys)})."));
            }
        }
    }

    // Enrichment targets (properties tagged [IversonSummary]/[IversonKeywords]/[IversonExtracted])
    // are columns the enrichment pipeline writes to, not reads from. Five rules keep that
    // invariant sound at registration time rather than failing obscurely at ingest.
    private static void ValidateEnrichmentTargets(TypeDescriptor typeDesc, SchemaDescriptor descriptor)
    {
        var ownerField = descriptor.Authorization?.OwnerField;

        foreach (var target in descriptor.EnrichmentTargets)
        {
            var property = typeDesc.Properties.First(p =>
                string.Equals(p.Name, target.ColumnName, StringComparison.OrdinalIgnoreCase));

            if (property.ClrType != ClrType.ClrString)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' must be a string " +
                    $"property; it is '{property.ClrType}'."));
            }

            if (property.IsKey ||
                string.Equals(target.ColumnName, descriptor.TenantColumn, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(ownerField) &&
                 string.Equals(target.ColumnName, ownerField, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot be the key, " +
                    "tenant_field, or owner_field."));
            }

            if (property.IsEmbedding || property.IsChunk)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' cannot also carry " +
                    "[IversonEmbedding]/[IversonChunk]: if this property were also a source property, the " +
                    "enrichment writeback would mutate the hashed text, causing the enricher to re-enrich its " +
                    "own republished event without bound."));
            }

            if (target.Kind == EnrichmentKind.Extracted && string.IsNullOrWhiteSpace(target.Hint))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Enrichment target '{target.ColumnName}' on '{descriptor.TypeName}' has [IversonExtracted] " +
                    "with an empty hint."));
            }
        }

        if (descriptor.EnrichmentTargets.Count > 0)
        {
            var targetNames = descriptor.EnrichmentTargets
                .Select(t => t.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Source text is defined as the concatenation of the type's [IversonEmbedding]/
            // [IversonChunk] properties — nothing else counts. A type with an enrichment target
            // but no embedding/chunk property would hash an empty source text and call the
            // enrichment model with an empty prompt.
            var hasSourceProperty = typeDesc.Properties.Any(p => p.IsEmbedding || p.IsChunk);

            if (!hasSourceProperty)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"'{descriptor.TypeName}' declares enrichment targets but has no source text property " +
                    "for the enrichment pipeline to read from."));
            }
        }
    }

    private static void ValidateIdentifier(string name, string context)
    {
        if (!IdentifierPattern.IsMatch(name))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{context} '{name}' is not a valid identifier — it must start with a letter and contain only letters and digits."));
        }
    }
}
