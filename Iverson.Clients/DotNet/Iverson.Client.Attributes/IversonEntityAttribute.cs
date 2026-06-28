namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a class as an Iverson-managed entity. The client framework will register
/// this type in the EntityRegistry and make it available for all coordinator operations.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IversonEntityAttribute(string? name = null) : Attribute
{
    /// <summary>
    /// Overrides the type name used in gRPC requests. Defaults to the class name.
    /// </summary>
    public string? Name { get; } = name;
}
