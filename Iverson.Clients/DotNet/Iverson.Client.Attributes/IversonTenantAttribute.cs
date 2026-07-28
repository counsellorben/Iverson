namespace Iverson.Client.Attributes;

/// <summary>
/// Marks the property holding the row's tenant id. The server requires every schema to
/// declare a tenant boundary and rejects registration without one, so exactly one property
/// per entity must carry this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonTenantAttribute : Attribute;
