namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a string property as the source text for automatic keyword extraction
/// during ingest enrichment.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonKeywordsAttribute : Attribute;
