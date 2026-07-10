namespace Iverson.StarRocks;

/// <summary>
/// The subset of a type's schema that StarRocksQueryBuilder/StarRocksPipelineBuilder need to
/// generate SQL: the physical table name, the primary key column, and the scalar column list.
/// Deliberately does not carry FK/vector/chunk/relation information — those never influence
/// query generation. Adapted from Iverson.Api.Schema.SchemaDescriptor at the API boundary by
/// SchemaBuilder.ToStarRocksQuerySchema; this project has no dependency on that type.
/// </summary>
public sealed record StarRocksQuerySchema(
    string TypeName,
    string TableName,
    string KeyColumnName,
    IReadOnlyList<string> ColumnNames);
