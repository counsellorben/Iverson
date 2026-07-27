namespace Iverson.Embeddings;

public sealed class EnrichmentServiceOptions
{
    public const string Section = "Enrichment";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ModelId { get; set; } = "qwen2.5:3b";
    public bool Enabled { get; set; } = true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
