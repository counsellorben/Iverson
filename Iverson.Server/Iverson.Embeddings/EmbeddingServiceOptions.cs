namespace Iverson.Embeddings;

public sealed class EmbeddingServiceOptions
{
    public const string Section = "Embeddings";

    public string ApiKey  { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
}
