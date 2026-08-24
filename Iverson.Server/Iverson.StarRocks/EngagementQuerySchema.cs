namespace Iverson.StarRocks;

/// <summary>
/// The subset of a type's schema that StarRocksQueryBuilder/StarRocksPipelineBuilder need to
/// generate SQL: the physical table name, the primary key column, and the scalar column list.
/// Deliberately does not carry FK/vector/chunk/relation information — those never influence
/// query generation. Adapted from Iverson.Api.Schema.SchemaDescriptor at the API boundary by
/// SchemaBuilder.ToEngagementQuerySchema; this project has no dependency on that type.
/// </summary>
/// <param name="ColumnNames">
/// Every scalar column, INCLUDING the server-owned tenant column: the read-time tenant predicate
/// is generated against a real physical column, so it must be in this list. Which of these columns
/// may be PROJECTED or REFERENCED by a caller is decided separately — see
/// <paramref name="TenantColumnName"/>.
/// </param>
/// <param name="TenantColumnName">
/// The SERVER-OWNED tenant column's name, or null for a schema that has none. Carried as DATA
/// rather than referenced as a constant because this project cannot see
/// <c>Iverson.Api.Schema.SchemaDescriptor.TenantColumnName</c> — it has no project reference to
/// Iverson.Api, and duplicating the string literal would break the "spelled once" rule.
/// <para>
/// THIS IS AN EXCLUSION KEY, NOT "WHICHEVER COLUMN CARRIES THE BOUNDARY" (Ruling 70).
/// SchemaBuilder.ToEngagementQuerySchema populates it from <c>SchemaDescriptor.TenantColumn</c>
/// ONLY WHEN that value is the reserved server-owned spelling, and passes null otherwise — which
/// is how this project keys its exclusions on the same reserved name every exclusion in
/// Iverson.Api is keyed on, without being able to see the constant. A legacy pre-cutover schema
/// rehydrated with a CLIENT-DECLARED tenant column (e.g. "TenantId") therefore arrives here with
/// null, and its column stays projectable, filterable and sortable — exactly as Iverson.Api's
/// <c>AuthorizationFieldMasking.RemoveTenantColumn</c> leaves it alone, because it is part of the
/// client's own declared contract.
/// </para>
/// <para>
/// Null here never widens the tenant boundary: the read-time tenant predicate is spliced from
/// <c>AuthorizationConstraint.TenantColumn</c> by BuildSearch/BuildAggregate/BuildGroupBy and
/// StarRocksPipelineBuilder, and never consults this field or
/// <c>StarRocksQueryBuilder.ResolveColumn</c>.
/// </para>
/// </param>
public sealed record EngagementQuerySchema(
    string TypeName,
    string TableName,
    string KeyColumnName,
    IReadOnlyList<string> ColumnNames,
    string? TenantColumnName = null)
{
    /// <summary>
    /// True when <paramref name="name"/> is this schema's server-owned tenant column.
    /// Case-insensitive, matching every other column-name comparison in query generation, so a
    /// caller cannot smuggle the name past an exclusion site by re-casing it.
    /// </summary>
    public bool IsTenantColumn(string name) =>
        TenantColumnName is not null &&
        string.Equals(name, TenantColumnName, StringComparison.OrdinalIgnoreCase);
}
