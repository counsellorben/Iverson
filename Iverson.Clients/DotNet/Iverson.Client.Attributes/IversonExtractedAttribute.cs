namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a string property as an LLM-driven structured extraction target during
/// ingest enrichment. The hint is REQUIRED — the server treats an empty hint as
/// "not an extraction target at all" and silently drops the target, so this
/// attribute cannot be applied without one.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonExtractedAttribute(string hint) : Attribute
{
    public string Hint { get; } = hint;
}
