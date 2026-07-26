namespace Iverson.Client.Attributes;

/// <summary>
/// Attaches human-readable description text to an entity type or one of its properties.
/// The text is carried to the server as part of the registered schema.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = false)]
public sealed class IversonDescriptionAttribute(string description) : Attribute
{
    /// <summary>
    /// The description text for the annotated type or property.
    /// </summary>
    public string Description { get; } = description;
}
