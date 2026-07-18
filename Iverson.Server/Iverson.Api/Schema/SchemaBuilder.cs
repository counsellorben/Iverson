using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using ContractsRelationKind = Iverson.Client.Contracts.RelationKind;
using SchemaRelationKind    = Iverson.Api.Schema.RelationKind;
using ContractsAuthorizationRules = Iverson.Client.Contracts.AuthorizationRules;
using SchemaAuthorizationRules    = Iverson.Api.Schema.AuthorizationRules;
using ContractsRowPermission      = Iverson.Client.Contracts.RowPermission;
using SchemaRowPermission         = Iverson.Api.Schema.RowPermission;
using ContractsFieldPermission    = Iverson.Client.Contracts.FieldPermission;
using SchemaFieldPermission       = Iverson.Api.Schema.FieldPermission;

namespace Iverson.Api.Schema;

internal static class SchemaBuilder
{
    internal static SchemaDescriptor BuildDescriptor(TypeDescriptor typeDesc, IEmbeddingService embedding)
    {
        var tableName = typeDesc.TypeName.ToSnakeCase() + "s";

        var keyProp = typeDesc.Properties.FirstOrDefault(p => p.IsKey)
            ?? throw new InvalidOperationException($"No key property on '{typeDesc.TypeName}'.");

        var scalars     = new List<ColumnDescriptor>();
        var fks         = new List<ForeignKeyDescriptor>();
        var vectors     = new List<VectorDescriptor>();
        var chunks      = new List<ChunkDescriptor>();
        var searchKeysSorted = new List<(string Name, int Order)>();
        var largeFields      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in typeDesc.Properties.Where(p => !p.IsKey))
        {
            var sqlType = ClrTypeToSql(prop.ClrType, prop.IsArray);
            scalars.Add(new ColumnDescriptor(prop.Name, sqlType, prop.IsNullable));

            if (prop.IsEmbedding)
            {
                vectors.Add(new VectorDescriptor(prop.Name, embedding.Dimension, embedding.ModelId));
                largeFields.Add(prop.Name);
            }

            if (prop.IsChunk)
            {
                chunks.Add(new ChunkDescriptor(prop.Name, prop.ChunkMaxTokens, prop.ChunkOverlap, embedding.ModelId, embedding.Dimension));
                largeFields.Add(prop.Name);
            }

            if (prop.IsLargeField)
                largeFields.Add(prop.Name);

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
            TenantColumn      = string.IsNullOrEmpty(typeDesc.TenantField) ? null : typeDesc.TenantField
        };
    }

    internal static TableSchema ToTableSchema(SchemaDescriptor d) => new(
        d.TableName,
        ToColumnSchema(d.KeyColumn),
        d.ScalarColumns.Select(ToColumnSchema).ToList(),
        d.TenantColumn);

    internal static ColumnSchema ToColumnSchema(ColumnDescriptor c) =>
        new(c.Name, c.SqlType, c.IsNullable);

    internal static StarRocksTableSchema ToStarRocksTableSchema(SchemaDescriptor d) => new(
        d.TableName,
        new StarRocksColumnSchema(d.KeyColumn.Name, ClrTypeToStarRocksType(d.KeyColumn.SqlType), false),
        d.ScalarColumns
            .Select(c => new StarRocksColumnSchema(c.Name, ClrTypeToStarRocksType(c.SqlType), c.IsNullable))
            .ToList())
    {
        SortKey = d.SearchKeyColumns
    };

    internal static StarRocksQuerySchema ToStarRocksQuerySchema(SchemaDescriptor d) => new(
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
        d.VectorFields.Select(v => new NamedVector($"{v.PropertyName.ToSnakeCase()}_vector", v.Dimension)).ToList(),
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

    // Only ClrGuid and ClrFloat have array-specific SQL/StarRocks representations distinct
    // from their scalar form (preserves the exact prior behavior of the three switches this
    // table replaces — every other ClrType's array variant reused its scalar mapping).
    private static readonly IReadOnlyDictionary<ClrType, ClrTypeMapping> ArrayTypeOverrides =
        new Dictionary<ClrType, ClrTypeMapping>
        {
            [ClrType.ClrGuid]  = new("UUID[]", "STRING", PayloadIndexKind.Keyword),
            [ClrType.ClrFloat] = new("REAL[]", "STRING", PayloadIndexKind.Keyword)
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

    internal static string ClrTypeToStarRocksType(string sqlType) =>
        SqlTypeMap.TryGetValue(sqlType, out var mapping) ? mapping.StarRocksType : "STRING";

    internal static PayloadIndexKind SqlTypeToPayloadKind(string sqlType) =>
        SqlTypeMap.TryGetValue(sqlType, out var mapping) ? mapping.PayloadKind : PayloadIndexKind.Keyword;
}
