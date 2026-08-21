using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using ContractsRelationKind       = Iverson.Client.Contracts.RelationKind;
using SchemaRelationKind          = Iverson.Api.Schema.RelationKind;
using ContractsAuthorizationRules = Iverson.Client.Contracts.AuthorizationRules;
using SchemaAuthorizationRules    = Iverson.Api.Schema.AuthorizationRules;
using ContractsRowPermission      = Iverson.Client.Contracts.RowPermission;
using SchemaRowPermission         = Iverson.Api.Schema.RowPermission;
using ContractsFieldPermission    = Iverson.Client.Contracts.FieldPermission;
using SchemaFieldPermission       = Iverson.Api.Schema.FieldPermission;

namespace Iverson.Api.Schema;

internal static class SchemaBuilder
{
    // Payload keys IntelligenceStoreConsumer writes on every chunk point. A metadata column
    // whose camelCase name lands on one of these is rejected at registration rather than
    // skipped at ingest: a skip leaves the column silently un-denormalized while
    // ObjectSearchGrpcService.BuildChunksFilter still accepts filters on it, so the filter
    // would match against the reserved key's value (e.g. the chunk passage text) instead.
    // Rejecting here keeps the ingest and search paths in agreement by construction.
    private static readonly HashSet<string> s_reservedChunkPayloadKeys =
        new(StringComparer.Ordinal) { "text", "parent_id", "field", "chunk_index" };

    internal static SchemaDescriptor BuildDescriptor(TypeDescriptor typeDesc, IEmbeddingService embedding)
    {
        var tableName = typeDesc.TypeName.ToSnakeCase() + "s";

        var keyProp = typeDesc.Properties.FirstOrDefault(p => p.IsKey)
            ?? throw new InvalidOperationException($"No key property on '{typeDesc.TypeName}'.");

        var scalars          = new List<ColumnDescriptor>();
        var fks              = new List<ForeignKeyDescriptor>();
        var vectors          = new List<VectorDescriptor>();
        var chunks           = new List<ChunkDescriptor>();
        var searchKeysSorted = new List<(string Name, int Order)>();
        var largeFields      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataColumns  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fieldDescriptions = new Dictionary<string, string>();
        var badMetadata      = new List<string>();
        var reservedMetadata = new List<string>();
        var enrichmentTargets = new List<EnrichmentTarget>();

        // [IversonDescription] is valid on any property including the key, so descriptions are
        // collected across all properties — unlike every other collection below, which is
        // built from non-key properties only.
        if (!string.IsNullOrEmpty(keyProp.Description))
            fieldDescriptions[keyProp.Name] = keyProp.Description;

        foreach (var prop in typeDesc.Properties.Where(p => !p.IsKey))
        {
            var sqlType = ClrTypeToSql(prop.ClrType, prop.IsArray);
            scalars.Add(
                new ColumnDescriptor(prop.Name, sqlType, prop.IsNullable));

            if (prop.IsEmbedding)
            {
                vectors.Add(
                    new VectorDescriptor(prop.Name, embedding.Dimension, embedding.ModelId));
                largeFields.Add(prop.Name);
            }

            if (prop.IsChunk)
            {
                chunks.Add(
                    new ChunkDescriptor(
                        prop.Name,
                        prop.ChunkMaxTokens,
                        prop.ChunkOverlap,
                        embedding.ModelId,
                        embedding.Dimension,
                        prop.ChunkContextual));
                largeFields.Add(prop.Name);
            }

            if (prop.IsSummaryTarget)
                enrichmentTargets.Add(new EnrichmentTarget(prop.Name, EnrichmentKind.Summary, null));

            if (prop.IsKeywordsTarget)
                enrichmentTargets.Add(new EnrichmentTarget(prop.Name, EnrichmentKind.Keywords, null));

            if (!string.IsNullOrEmpty(prop.ExtractHint))
                enrichmentTargets.Add(new EnrichmentTarget(prop.Name, EnrichmentKind.Extracted, prop.ExtractHint));

            if (prop.IsLargeField)
                largeFields.Add(prop.Name);

            if (prop.IsMetadata)
            {
                metadataColumns.Add(prop.Name);
                if (prop.IsEmbedding || prop.IsChunk || prop.IsArray || prop.IsLargeField)
                    badMetadata.Add(prop.Name);
                if (s_reservedChunkPayloadKeys.Contains(prop.Name.ToCamelCase()))
                    reservedMetadata.Add(prop.Name);
            }

            if (!string.IsNullOrEmpty(prop.Description))
                fieldDescriptions[prop.Name] = prop.Description;

            if (prop.IsSearchKey)
                searchKeysSorted.Add((prop.Name, prop.SearchKeyOrder));

            if (prop.Name.EndsWith("Id",  StringComparison.OrdinalIgnoreCase) ||
                prop.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))
            {
                var relatedType = typeDesc.Relations
                    .FirstOrDefault(r => r.ForeignKey == prop.Name)?.RelatedType ?? string.Empty;
                fks.Add(new ForeignKeyDescriptor(prop.Name, relatedType));
            }
        }

        searchKeysSorted.Sort((a, b) => a.Order.CompareTo(b.Order));

        var conflicts = searchKeysSorted.Where(sk => largeFields.Contains(sk.Name)).Select(sk => sk.Name).ToList();
        if (conflicts.Count > 0)
            throw new InvalidOperationException(conflicts.Count == 1
                ? $"Property '{conflicts[0]}' cannot have both [IversonSearchKey] and a large-field annotation."
                : $"Properties {string.Join(", ", conflicts.Select(n => $"'{n}'"))} cannot have both [IversonSearchKey] and a large-field annotation.");

        if (badMetadata.Count > 0)
            throw new InvalidOperationException(badMetadata.Count == 1
                ? $"Property '{badMetadata[0]}' cannot have both [IversonMetadata] and an embedding, chunk, array, or large-field annotation."
                : $"Properties {string.Join(", ", badMetadata.Select(n => $"'{n}'"))} cannot have both [IversonMetadata] and an embedding, chunk, array, or large-field annotation.");

        if (reservedMetadata.Count > 0)
            throw new InvalidOperationException(reservedMetadata.Count == 1
                ? $"Property '{reservedMetadata[0]}' cannot have [IversonMetadata]: its payload key collides with a reserved chunk payload key ({string.Join(", ", s_reservedChunkPayloadKeys)})."
                : $"Properties {string.Join(", ", reservedMetadata.Select(n => $"'{n}'"))} cannot have [IversonMetadata]: their payload keys collide with reserved chunk payload keys ({string.Join(", ", s_reservedChunkPayloadKeys)}).");

        var relations = typeDesc.Relations.Select(r => new RelationDescriptor(
            r.PropertyName,
            r.Kind switch
            {
                ContractsRelationKind.OneToOne   => SchemaRelationKind.OneToOne,
                ContractsRelationKind.OneToMany  => SchemaRelationKind.OneToMany,
                ContractsRelationKind.ManyToOne  => SchemaRelationKind.ManyToOne,
                ContractsRelationKind.ManyToMany => SchemaRelationKind.ManyToMany,
                _ => throw new ArgumentOutOfRangeException(nameof(r.Kind), r.Kind,
                    $"Unhandled {nameof(ContractsRelationKind)} value — add a case above.")
            },
            r.RelatedType,
            r.ForeignKey
        )).ToList();

        DocumentTemplate? documentTemplate = null;
        if (!string.IsNullOrEmpty(typeDesc.DocumentTemplate))
        {
            documentTemplate = DocumentTemplateParser.Parse(typeDesc.DocumentTemplate);

            // proto3 scalars default to 0 when unset, and no client emits these fields yet.
            // SplitIntoChunks computes step = max(maxChars - overlapChars, maxChars / 2), which is 0
            // when maxTokens is 0 — an infinite loop awaiting one embedding call per iteration.
            var maxTokens = typeDesc.DocumentMaxTokens > 0 ? typeDesc.DocumentMaxTokens : 512;
            var overlap   = typeDesc.DocumentOverlap  > 0 ? typeDesc.DocumentOverlap  : 64;

            chunks.Add(new ChunkDescriptor(
                "Document",
                maxTokens,
                overlap,
                embedding.ModelId,
                embedding.Dimension,
                typeDesc.DocumentContextual));
        }

        ContractsAuthorizationRules? contractsAuthorization = typeDesc.Authorization;
        var authorization = contractsAuthorization is null
            ? null
            : new SchemaAuthorizationRules(
                contractsAuthorization.OwnerField,
                contractsAuthorization.RowPermissions.Select((ContractsRowPermission rp) => new SchemaRowPermission(
                    rp.Role, rp.CanReadAll, rp.CanWriteAll, rp.CanDeleteAll)).ToList(),
                contractsAuthorization.FieldPermissions.Select((ContractsFieldPermission fp) => new SchemaFieldPermission(
                    fp.FieldName, fp.ReadableRoles.ToList(), fp.WritableRoles.ToList())).ToList());

        return new SchemaDescriptor
        {
            TypeName          = typeDesc.TypeName,
            TableName         = tableName,
            CollectionName    = (vectors.Count > 0 || chunks.Count > 0) ? tableName : null,
            KeyColumn         = new ColumnDescriptor(keyProp.Name, ClrTypeToSql(keyProp.ClrType, false), false),
            ScalarColumns     = scalars,
            FkColumns         = fks,
            VectorFields      = vectors,
            ChunkFields       = chunks,
            Relations         = relations,
            SearchKeyColumns  = searchKeysSorted.ConvertAll(sk => sk.Name),
            LargeFieldColumns = largeFields,
            Authorization     = authorization,
            TenantColumn      = string.IsNullOrEmpty(typeDesc.TenantField) ? null : typeDesc.TenantField,
            MetadataColumns   = metadataColumns,
            Description       = string.IsNullOrEmpty(typeDesc.Description) ? null : typeDesc.Description,
            FieldDescriptions = fieldDescriptions,
            EnrichmentTargets = enrichmentTargets,
            DocumentTemplate       = documentTemplate,
            DocumentTemplateSource = string.IsNullOrEmpty(typeDesc.DocumentTemplate) ? null : typeDesc.DocumentTemplate
        };
    }

    internal static TableSchema ToTableSchema(SchemaDescriptor d) => new(
        d.TableName,
        ToColumnSchema(d.KeyColumn),
        d.ScalarColumns.Select(ToColumnSchema).ToList(),
        d.TenantColumn);

    internal static ColumnSchema ToColumnSchema(ColumnDescriptor c) =>
        new(c.Name, c.SqlType, c.IsNullable);

    internal static EngagementTableSchema ToEngagementTableSchema(SchemaDescriptor d) => new(
        d.TableName,
        new EngagementColumnSchema(d.KeyColumn.Name, ClrTypeToEngagementType(d.KeyColumn.SqlType), false),
        d.ScalarColumns
            .Select(c => new EngagementColumnSchema(c.Name, ClrTypeToEngagementType(c.SqlType), c.IsNullable))
            .ToList())
    {
        SortKey = d.SearchKeyColumns
    };

    internal static EngagementQuerySchema ToEngagementQuerySchema(SchemaDescriptor d) => new(
        d.TypeName,
        d.TableName,
        d.KeyColumn.Name,
        d.ScalarColumns.Select(c => c.Name).ToList());

    internal static CollectionSchema ToChunkCollectionSchema(SchemaDescriptor d)
    {
        var indexes = new List<PayloadIndex> { new("parent_id", PayloadIndexKind.Keyword) };
        if (d.Authorization?.OwnerField is { } ownerField)
            indexes.Add(new PayloadIndex(ownerField.ToCamelCase(), PayloadIndexKind.Keyword));

        return new CollectionSchema(
            d.CollectionName! + "_chunks",
            d.ChunkFields.Select(c => new NamedVector($"{c.PropertyName.ToSnakeCase()}_vector", c.Dimension)).ToList(),
            indexes);
    }

    internal static CollectionSchema ToCollectionSchema(SchemaDescriptor d) => new(
        d.CollectionName!,
        d.VectorFields.Select(v => new NamedVector($"{v.PropertyName.ToSnakeCase()}_vector", v.Dimension))
            .Concat(d.ChunkFields.Select(c => new NamedVector($"{c.PropertyName.ToSnakeCase()}_centroid", c.Dimension)))
            .ToList(),
        d.ScalarColumns
            .Select(c => new PayloadIndex(c.Name.ToCamelCase(), SqlTypeToPayloadKind(c.SqlType)))
            .Concat(d.FkColumns.Select(fk => new PayloadIndex(fk.ColumnName.ToCamelCase(), PayloadIndexKind.Keyword)))
            .ToList());

    private readonly record struct ClrTypeMapping(string SqlType, string StarRocksType, PayloadIndexKind PayloadKind);

    // Single source of truth for scalar ClrType → (SQL type, StarRocks type, Qdrant payload
    // index kind). Adding a new ClrType means adding one entry here — ClrTypeToSql,
    // ClrTypeToStarRocksType, and SqlTypeToPayloadKind all derive from this one table instead
    // of three independently-maintained switches.
    private static readonly IReadOnlyDictionary<ClrType, ClrTypeMapping> ScalarTypeMap =
        new Dictionary<ClrType, ClrTypeMapping>
        {
            [ClrType.ClrGuid]     = new("UUID", "VARCHAR(36)", PayloadIndexKind.Keyword),
            [ClrType.ClrString]   = new("TEXT", "STRING", PayloadIndexKind.Keyword),
            [ClrType.ClrInt32]    = new("INTEGER", "INT", PayloadIndexKind.Integer),
            [ClrType.ClrInt64]    = new("BIGINT", "BIGINT", PayloadIndexKind.Integer),
            [ClrType.ClrFloat]    = new("REAL", "FLOAT", PayloadIndexKind.Float),
            [ClrType.ClrDouble]   = new("DOUBLE PRECISION", "DOUBLE", PayloadIndexKind.Float),
            [ClrType.ClrBool]     = new("BOOLEAN", "BOOLEAN", PayloadIndexKind.Boolean),
            [ClrType.ClrDatetime] = new("TIMESTAMPTZ", "DATETIME", PayloadIndexKind.Datetime),
            [ClrType.ClrBytes]    = new("BYTEA", "VARBINARY", PayloadIndexKind.Keyword)
        };

    // Total over ClrType. StarRocks is STRING for every array. Payload kinds are element-typed
    // except ClrFloat, which keeps Keyword — see the comment on that row below.
    private static readonly IReadOnlyDictionary<ClrType, ClrTypeMapping> ArrayTypeOverrides =
        new Dictionary<ClrType, ClrTypeMapping>
        {
            [ClrType.ClrGuid]     = new("UUID[]", "STRING", PayloadIndexKind.Keyword),
            [ClrType.ClrString]   = new("TEXT[]", "STRING", PayloadIndexKind.Keyword),
            [ClrType.ClrInt32]    = new("INTEGER[]", "STRING", PayloadIndexKind.Integer),
            [ClrType.ClrInt64]    = new("BIGINT[]", "STRING", PayloadIndexKind.Integer),
            // Keyword, not Float: preserved from the pre-existing entry because changing it
            // would retype a live Qdrant index. See the spec's §1 and "Out of scope".
            [ClrType.ClrFloat]    = new("REAL[]", "STRING", PayloadIndexKind.Keyword),
            [ClrType.ClrDouble]   = new("DOUBLE PRECISION[]", "STRING", PayloadIndexKind.Float),
            [ClrType.ClrBool]     = new("BOOLEAN[]", "STRING", PayloadIndexKind.Boolean),
            [ClrType.ClrDatetime] = new("TIMESTAMPTZ[]", "STRING", PayloadIndexKind.Datetime),
            // Reachable only via byte[][] — byte[] is carved out as a scalar at
            // Iverson.Clients/DotNet/Iverson.Client.Core/SchemaRegistrar.cs:239. Present so the
            // table is total over the enum.
            [ClrType.ClrBytes]    = new("BYTEA[]", "STRING", PayloadIndexKind.Keyword)
        };

    // Derived from ScalarTypeMap + ArrayTypeOverrides at static-init time, keyed by the SQL
    // type string, so ClrTypeToStarRocksType/SqlTypeToPayloadKind — which only ever receive a
    // persisted SQL-type string, never the original ClrType (ColumnDescriptor.SqlType is what's
    // serialized into the _iverson_schema table) — stay consistent with ClrTypeToSql by
    // construction instead of by separately-maintained switch.
    private static readonly IReadOnlyDictionary<string, ClrTypeMapping> SqlTypeMap =
        ScalarTypeMap.Values
            .Concat(ArrayTypeOverrides.Values)
            .ToDictionary(m => m.SqlType, m => m, StringComparer.OrdinalIgnoreCase);

    internal static string ClrTypeToSql(ClrType t, bool isArray)
    {
        if (isArray && ArrayTypeOverrides.TryGetValue(t, out var arrayMapping))
            return arrayMapping.SqlType;

        return ScalarTypeMap.TryGetValue(t, out var mapping)
            ? mapping.SqlType
            : throw new ArgumentOutOfRangeException(nameof(t), t,
                $"Unhandled {nameof(ClrType)} value — add an entry to {nameof(SchemaBuilder)}.{nameof(ScalarTypeMap)}.");
    }

    // Inverse of ClrTypeToSql, for the GetSchema read path: a persisted ColumnDescriptor carries
    // only the SQL type string, but the catalog reports clr_type + is_array. Built from the same
    // two maps ClrTypeToSql reads, so the two cannot disagree.
    private static readonly IReadOnlyDictionary<string, (ClrType Type, bool IsArray)> SqlTypeToClrMap =
        ScalarTypeMap.Select(kv => (Sql: kv.Value.SqlType, Clr: kv.Key, IsArray: false))
            .Concat(ArrayTypeOverrides.Select(kv => (Sql: kv.Value.SqlType, Clr: kv.Key, IsArray: true)))
            .ToDictionary(x => x.Sql, x => (x.Clr, x.IsArray), StringComparer.OrdinalIgnoreCase);

    internal static (ClrType Type, bool IsArray) SqlTypeToClr(string sqlType) =>
        TrySqlTypeToClr(sqlType, out var mapping)
            ? mapping
            : throw new ArgumentOutOfRangeException(nameof(sqlType), sqlType,
                $"Unhandled SQL type — add an entry to {nameof(SchemaBuilder)}.{nameof(ScalarTypeMap)}.");

    /// <summary>
    /// Non-throwing form of <see cref="SqlTypeToClr"/>, for the GetSchema read path.
    /// <see cref="SchemaRegistry"/> rehydrates persisted descriptors written by older builds, so a
    /// column may carry a SQL type string this build no longer maps. Skipping that one column keeps
    /// discovery available for every other type, rather than failing the whole RPC — matching how
    /// <see cref="ClrTypeToEngagementType"/> and <see cref="SqlTypeToPayloadKind"/> degrade. The
    /// write path (<see cref="ClrTypeToSql"/>) still throws, where failing registration is correct.
    /// </summary>
    internal static bool TrySqlTypeToClr(string sqlType, out (ClrType Type, bool IsArray) mapping) =>
        SqlTypeToClrMap.TryGetValue(sqlType, out mapping);

    internal static string ClrTypeToEngagementType(string sqlType) =>
        SqlTypeMap.TryGetValue(sqlType, out var mapping) ? mapping.StarRocksType : "STRING";

    internal static PayloadIndexKind SqlTypeToPayloadKind(string sqlType) =>
        SqlTypeMap.TryGetValue(sqlType, out var mapping) ? mapping.PayloadKind : PayloadIndexKind.Keyword;
}
