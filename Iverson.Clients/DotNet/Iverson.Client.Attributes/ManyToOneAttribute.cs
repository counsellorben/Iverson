namespace Iverson.Client.Attributes;

/// <summary>
/// Declares a many-to-one relationship: many instances of this entity belong to a single
/// <paramref name="related"/> instance. The decorated property holds one related entity.
/// The GraphAssembler reads <paramref name="foreignKey"/> — a property on *this* entity —
/// to perform the lookup.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ManyToOneAttribute(Type related, string? foreignKey = null) : Attribute
{
    public Type Related { get; } = related;

    /// <summary>
    /// Name of the property on this entity that holds the related entity's key.
    /// If null, inferred as "{RelatedTypeName}Id".
    /// </summary>
    public string? ForeignKey { get; } = foreignKey;
}
