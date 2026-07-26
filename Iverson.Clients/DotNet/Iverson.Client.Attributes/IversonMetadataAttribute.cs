namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a property as a metadata signal. Metadata properties are denormalized
/// onto chunk points so they can be filtered and surfaced alongside search results.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonMetadataAttribute : Attribute;
