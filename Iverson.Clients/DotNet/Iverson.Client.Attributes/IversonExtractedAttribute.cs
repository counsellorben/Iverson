namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a string property as an LLM-driven structured extraction target during
/// ingest enrichment. The hint is REQUIRED — the server treats an empty hint as
/// "not an extraction target at all" and silently drops the target, so this
/// attribute cannot be applied without one.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonExtractedAttribute : Attribute
{
    public IversonExtractedAttribute(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            throw new ArgumentException(
                "[IversonExtracted] requires a non-blank extraction hint; the server treats an " +
                "empty hint as \"not an extraction target\" and silently drops the property, so it " +
                "would never be populated.",
                nameof(hint));

        Hint = hint;
    }

    public string Hint { get; }
}
