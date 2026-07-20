namespace Iverson.StarRocks;

internal static class StarRocksSchemaManager
{
    internal static string BuildCreateTableDdl(StarRocksTableSchema schema, string qualifiedTableName)
    {
        var keySql  = $"`{schema.KeyColumn.Name}` {schema.KeyColumn.SrType} NOT NULL";
        var colsSql = schema.Columns.Select(c =>
            $"`{c.Name}` {c.SrType}{(c.IsNullable ? "" : " NOT NULL")}");

        var orderBy = schema.SortKey.Count > 0
            ? $"\nORDER BY ({string.Join(", ", schema.SortKey.Select(k => $"`{k}`"))})"
            : "";

        return $"""
            CREATE TABLE IF NOT EXISTS {qualifiedTableName} (
                {keySql},
                {string.Join(",\n    ", colsSql)}
            ) ENGINE=OLAP
            PRIMARY KEY(`{schema.KeyColumn.Name}`)
            DISTRIBUTED BY HASH(`{schema.KeyColumn.Name}`) BUCKETS 4{orderBy}
            PROPERTIES ("replication_num" = "1")
            """;
    }
}
