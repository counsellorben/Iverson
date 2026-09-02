namespace Iverson.Api.Schema;

public sealed record SchemaDescriptor
{
    /// <summary>
    /// The one and only spelling of the server-owned tenant column. Defined here so no consumer
    /// carries a string literal: the name is never derived from, nor exposed to, a client — it is
    /// injected into <see cref="ScalarColumns"/> by <c>SchemaBuilder.BuildDescriptor</c> and is the
    /// value of <see cref="TenantColumn"/> for every schema this build registers.
    /// The leading double underscore is deliberate: it marks the column as server-reserved and
    /// keeps it visually distinct from any client-declared property in DDL and payload dumps.
    /// </summary>
    public const string TenantColumnName = "__TenantId";

    /// <summary>
    /// True when <paramref name="name"/> is the server-owned tenant column. Case-insensitive to
    /// match every other column-name comparison in the codebase, so a caller cannot smuggle the
    /// name past an exclusion site by re-casing it.
    /// </summary>
    public static bool IsTenantColumn(string name) =>
        string.Equals(name, TenantColumnName, StringComparison.OrdinalIgnoreCase);

    // A type's model lives on its vector and chunk fields, which exist only when it has embedding
    // or chunk properties — so null here means "this type has no embedded content", not "unknown".
    // Read by the re-registration guard and by the query path; one definition, because two copies
    // that disagree would reject a legal registration or embed a query with the wrong model.
    internal static string? ModelOf(SchemaDescriptor d) =>
        d.VectorFields.FirstOrDefault()?.ModelId ?? d.ChunkFields.FirstOrDefault()?.ModelId;

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

    // Non-nullable, and `required` — but READ THIS BEFORE TREATING THE ANNOTATION AS A RUNTIME
    // GUARANTEE, because it is not one on its own.
    //
    // What SchemaRegistry.LoadAsync does with a legacy row (Ruling 34): it deserializes every
    // _iverson_schema row with System.Text.Json, and nullable reference-type annotations are
    // ERASED at runtime. A row written before 63a577a (2026-07-17), when this property did not
    // exist, carries no `tenantColumn` key at all. Measured behaviour of System.Text.Json against
    // such a row:
    //   * without `required`: the property is silently left NULL, and the first dereference is a
    //     NullReferenceException — the annotation buys nothing;
    //   * with `required` and the key ABSENT: JsonException naming `tenantColumn`;
    //   * with `required` and the key present but EXPLICITLY NULL: deserializes to NULL anyway —
    //     `required` checks PRESENCE, never non-nullness.
    // So `required` alone still cannot make this claim true. What makes it true is the explicit
    // runtime check in SchemaRegistry.LoadAsync, which refuses to admit any descriptor whose
    // TenantColumn is null or empty and logs it. `required` is here for the OTHER mutation point:
    // it makes a hand-constructed descriptor that forgets the column a compile error, so
    // RegisterAsync cannot be the hole instead.
    //
    // Downstream consumers may therefore rely on this being a real column name. Every legacy
    // pre-cutover schema fails closed by never entering the registry at all.
    public required string TenantColumn { get; init; }

    // Defaulted, not required: SchemaRegistry.LoadAsync deserializes pre-change _iverson_schema
    // rows via JsonSerializer, and legacy rows predate the metadata layer and carry none of these
    // keys. Unlike TenantColumn above, an absent value here is benign — no boundary rests on it.
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

    // Nullable, not required: legacy _iverson_schema JSON rows predate the document-template
    // feature and carry neither key. Unlike TenantColumn above, an absent value here is benign —
    // no boundary rests on it, and null is a first-class "this type has no template".
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

// Shared by SchemaRegistrationOrchestrator (which rejects a colliding descriptor outright) and
// SchemaRegistry.LoadAsync (which cannot reject — a descriptor persisted before this check existed
// can still be sitting in Postgres, and refusing to load it would take down a running deployment on
// a legacy schema). Both call sites must agree on exactly what counts as a collision.
public static class RelationCollisionCheck
{
    public static bool IsCollision(RelationDescriptor relation) =>
        string.Equals(relation.PropertyName, relation.ForeignKey, StringComparison.OrdinalIgnoreCase);
}

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
