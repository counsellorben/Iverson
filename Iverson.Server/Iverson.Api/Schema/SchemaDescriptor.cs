namespace Iverson.Api.Schema;

public sealed record SchemaDescriptor
{
    public required string TypeName       { get; init; }
    public required string TableName      { get; init; }
    public string?         CollectionName { get; init; }

    public required ColumnDescriptor                    KeyColumn     { get; init; }
    public required IReadOnlyList<ColumnDescriptor>     ScalarColumns { get; init; }
    public required IReadOnlyList<ForeignKeyDescriptor> FkColumns     { get; init; }
    public required IReadOnlyList<VectorDescriptor>     VectorFields  { get; init; }
    public required IReadOnlyList<ChunkDescriptor>      ChunkFields   { get; init; }
    public required IReadOnlyList<RelationDescriptor>   Relations     { get; init; }

    public List<string>      SearchKeyColumns  { get; init; } = [];
    public HashSet<string>   LargeFieldColumns { get; init; } = [];

    public AuthorizationRules? Authorization { get; init; }

    // Nullable, not required: SchemaRegistry.LoadAsync deserializes pre-change _iverson_schema
    // rows via JsonSerializer; a required member missing from legacy JSON would throw at startup.
    // A null TenantColumn means a legacy (pre-cutover) schema — the evaluator denies all access
    // to it until it is re-registered with a tenant_field.
    public string? TenantColumn { get; init; }
}

public sealed record ColumnDescriptor(string Name, string SqlType, bool IsNullable);

public sealed record ForeignKeyDescriptor(string ColumnName, string ReferencedTypeName);

public sealed record VectorDescriptor(string PropertyName, int Dimension, string ModelId);

public sealed record ChunkDescriptor(string PropertyName, int MaxTokens, int Overlap, string ModelId, int Dimension);

public sealed record RelationDescriptor(
    string PropertyName,
    RelationKind Kind,
    string RelatedTypeName,
    string ForeignKey);

public enum RelationKind { OneToOne, OneToMany, ManyToOne, ManyToMany }

public sealed record AuthorizationRules(
    string? OwnerField,
    IReadOnlyList<RowPermission> RowPermissions,
    IReadOnlyList<FieldPermission> FieldPermissions);

public sealed record RowPermission(string Role, bool CanReadAll, bool CanWriteAll, bool CanDeleteAll);

public sealed record FieldPermission(
    string FieldName,
    IReadOnlyList<string> ReadableRoles,
    IReadOnlyList<string> WritableRoles);
