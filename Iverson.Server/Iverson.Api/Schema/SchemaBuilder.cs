using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using ContractsRelationKind = Iverson.Client.Contracts.RelationKind;
using SchemaRelationKind    = Iverson.Api.Schema.RelationKind;

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
                ContractsRelationKind.OneToOne  => SchemaRelationKind.OneToOne,
                ContractsRelationKind.OneToMany => SchemaRelationKind.OneToMany,
                ContractsRelationKind.ManyToOne => SchemaRelationKind.ManyToOne,
                _                               => SchemaRelationKind.ManyToMany
            },
            r.RelatedType,
            r.ForeignKey
        )).ToList();

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
            LargeFieldColumns = largeFields
        };
    }

    internal static TableSchema ToTableSchema(SchemaDescriptor d) => new(
        d.TableName,
        ToColumnSchema(d.KeyColumn),
        d.ScalarColumns.Select(ToColumnSchema).ToList());

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

    internal static string ClrTypeToStarRocksType(string sqlType) => sqlType.ToUpperInvariant() switch
    {
        "UUID"             => "VARCHAR(36)",
        "UUID[]"           => "STRING",
        "TEXT"             => "STRING",
        "INTEGER"          => "INT",
        "BIGINT"           => "BIGINT",
        "REAL"             => "FLOAT",
        "REAL[]"           => "STRING",
        "DOUBLE PRECISION" => "DOUBLE",
        "BOOLEAN"          => "BOOLEAN",
        "TIMESTAMPTZ"      => "DATETIME",
        "BYTEA"            => "VARBINARY",
        _                  => "STRING"
    };

    internal static CollectionSchema ToChunkCollectionSchema(SchemaDescriptor d) => new(
        d.CollectionName! + "_chunks",
        d.ChunkFields.Select(c => new NamedVector($"{c.PropertyName.ToSnakeCase()}_vector", c.Dimension)).ToList(),
        [new PayloadIndex("parent_id", PayloadIndexKind.Keyword)]);

    internal static CollectionSchema ToCollectionSchema(SchemaDescriptor d) => new(
        d.CollectionName!,
        d.VectorFields.Select(v => new NamedVector($"{v.PropertyName.ToSnakeCase()}_vector", v.Dimension)).ToList(),
        d.ScalarColumns
            .Select(c => new PayloadIndex(c.Name, SqlTypeToPayloadKind(c.SqlType)))
            .Concat(d.FkColumns.Select(fk => new PayloadIndex(fk.ColumnName, PayloadIndexKind.Keyword)))
            .ToList());

    internal static string ClrTypeToSql(ClrType t, bool isArray) => (t, isArray) switch
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

    internal static PayloadIndexKind SqlTypeToPayloadKind(string sqlType) => sqlType.ToUpperInvariant() switch
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
}
