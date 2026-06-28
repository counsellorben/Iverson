namespace Iverson.Client.Attributes;

/// <summary>
/// Declares a one-to-many relationship: this entity owns a collection of
/// <paramref name="related"/> instances. The decorated property must be a collection type.
/// The GraphAssembler uses <paramref name="foreignKey"/> — a property name on the related
/// type that points back to this entity — to fetch all children via a streamed retrieval.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class OneToManyAttribute(Type related, string? foreignKey = null) : Attribute
{
    public Type Related { get; } = related;

    /// <summary>
    /// Name of the property on the <paramref name="related"/> type that holds this entity's key.
    /// If null, inferred as "{ThisTypeName}Id".
    /// </summary>
    public string? ForeignKey { get; } = foreignKey;
}
