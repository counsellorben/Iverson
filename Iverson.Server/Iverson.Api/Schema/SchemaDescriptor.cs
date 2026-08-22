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

    // Defaulted, not required — same rationale as TenantColumn above: legacy _iverson_schema
    // JSON rows predate the metadata layer and carry none of these keys.
    // The comparer is re-applied in the init accessor rather than only at construction:
    // SchemaRegistry.LoadAsync deserializes this record with System.Text.Json, which builds a
    // plain HashSet<string> with the default case-SENSITIVE comparer. Without this, a
    // Contains("category") lookup would succeed in the process that registered the schema and
    // fail in every process that loaded it from Postgres.
    private readonly HashSet<string> _metadataColumns = [];
    public HashSet<string> MetadataColumns
    {
        get => _metadataColumns;
        init => _metadataColumns = new HashSet<string>(value ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public string?                    Description       { get; init; }
    public Dictionary<string, string> FieldDescriptions { get; init; } = [];

    public IReadOnlyList<EnrichmentTarget> EnrichmentTargets { get; init; } = [];

    // Nullable, not required — same rationale as TenantColumn above: legacy _iverson_schema
    // JSON rows predate the document-template feature and carry neither key.
    //
    // DocumentTemplateSource is the raw template string and is what schema-drift detection
    // diffs against: record equality over DocumentTemplate's parsed segment list is
    // reference-based (EqualityComparer<T>.Default on an IReadOnlyList<T> member), so comparing
    // parsed models would report every registration as changed even when the source text is
    // identical.
    public DocumentTemplate? DocumentTemplate       { get; init; }
    public string?           DocumentTemplateSource { get; init; }
}

public enum EnrichmentKind { Summary, Keywords, Extracted }

public sealed record EnrichmentTarget(string ColumnName, EnrichmentKind Kind, string? Hint);

public sealed record ColumnDescriptor(string Name, string SqlType, bool IsNullable);

public sealed record ForeignKeyDescriptor(string ColumnName, string ReferencedTypeName);

public sealed record VectorDescriptor(string PropertyName, int Dimension, string ModelId);

public sealed record ChunkDescriptor(
    string PropertyName, int MaxTokens, int Overlap, string ModelId, int Dimension,
    bool Contextual = false);

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
