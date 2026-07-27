namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a string property as the source text for automatic summary generation
/// during ingest enrichment.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonSummaryAttribute : Attribute;
