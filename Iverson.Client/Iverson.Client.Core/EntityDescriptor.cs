using System.Reflection;

namespace Iverson.Client.Core;

public sealed class EntityDescriptor
{
    public required Type                             EntityType  { get; init; }
    public required string                           EntityName  { get; init; }
    public required PropertyInfo                     KeyProperty { get; init; }
    public required IReadOnlyList<RelationDescriptor> Relations { get; init; }

    public string GetKeyString(object entity) =>
        KeyProperty.GetValue(entity)?.ToString()
        ?? throw new InvalidOperationException(
            $"[IversonKey] on {EntityType.Name}.{KeyProperty.Name} returned null.");
}
