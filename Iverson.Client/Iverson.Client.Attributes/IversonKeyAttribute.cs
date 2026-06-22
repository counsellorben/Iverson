namespace Iverson.Client.Attributes;

/// <summary>
/// Marks the universal identifier property for an entity. This key is sent as the
/// lookup key in all gRPC requests and used by the GraphAssembler to resolve relations.
/// Exactly one property per entity class must carry this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonKeyAttribute : Attribute;
