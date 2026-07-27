namespace Iverson.Embeddings;

public sealed class EnrichmentServiceOptions
{
    public const string Section = "Enrichment";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ModelId { get; set; } = "qwen2.5:3b";
    public bool Enabled { get; set; } = true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Caps how many contextual chunk-prefix generations may be in flight at once for a single
    /// chunk field. Generative calls are far more expensive than embedding calls, and a large text
    /// field can split into hundreds of chunks; without a cap all of them hit Ollama simultaneously,
    /// it serializes them, the tail hits <see cref="Timeout"/>, and the consumer's Kafka partition
    /// stalls for minutes behind one message.
    /// </summary>
    public int MaxConcurrentChunkPrefixes { get; set; } = 4;
}
