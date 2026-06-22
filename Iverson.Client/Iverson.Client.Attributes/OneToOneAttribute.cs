namespace Iverson.Client.Attributes;

/// <summary>
/// Declares a one-to-one relationship between this entity and <paramref name="related"/>.
/// The decorated property holds a single instance of the related entity.
/// The GraphAssembler performs a single retrieval using <paramref name="foreignKey"/>
/// to populate this property after a fetch.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class OneToOneAttribute(Type related, string? foreignKey = null) : Attribute
{
    public Type Related { get; } = related;

    /// <summary>
    /// Name of the property on this entity that holds the related entity's key.
    /// If null, the assembler infers it as "{RelatedTypeName}Id".
    /// </summary>
    public string? ForeignKey { get; } = foreignKey;
}
