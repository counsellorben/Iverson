namespace Iverson.Client.Attributes;

public static class EmbeddingModelExtensions
{
    public static int GetDimension(this EmbeddingModel model) => model switch
    {
        EmbeddingModel.OpenAiTextEmbedding3Small      => 1536,
        EmbeddingModel.OpenAiTextEmbedding3Large      => 3072,
        EmbeddingModel.OpenAiTextEmbeddingAda002      => 1536,

        EmbeddingModel.CohereEmbedEnglishV3           => 1024,
        EmbeddingModel.CohereEmbedMultilingualV3      => 1024,
        EmbeddingModel.CohereEmbedEnglishLightV3      => 384,
        EmbeddingModel.CohereEmbedMultilingualLightV3 => 384,

        EmbeddingModel.GoogleTextEmbedding004         => 768,
        EmbeddingModel.GoogleGeminiEmbedding          => 3072,

        EmbeddingModel.MistralEmbed                   => 1024,

        EmbeddingModel.VoyageV3                       => 1024,
        EmbeddingModel.VoyageV3Lite                   => 512,
        EmbeddingModel.VoyageLarge2                   => 1536,

        EmbeddingModel.NomicEmbedText                 => 768,
        EmbeddingModel.AllMiniLmL6V2                  => 384,
        EmbeddingModel.BgeLargeEnV15                  => 1024,
        EmbeddingModel.BgeM3                          => 1024,
        EmbeddingModel.MxbaiEmbedLarge                => 1024,

        EmbeddingModel.Custom => throw new InvalidOperationException(
            "EmbeddingModel.Custom requires an explicit dimension. " +
            "Use [IversonVector(EmbeddingModel.Custom, dimension: N)]."),

        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown embedding model.")
    };

    public static string GetModelId(this EmbeddingModel model) => model switch
    {
        EmbeddingModel.OpenAiTextEmbedding3Small      => "text-embedding-3-small",
        EmbeddingModel.OpenAiTextEmbedding3Large      => "text-embedding-3-large",
        EmbeddingModel.OpenAiTextEmbeddingAda002      => "text-embedding-ada-002",

        EmbeddingModel.CohereEmbedEnglishV3           => "embed-english-v3.0",
        EmbeddingModel.CohereEmbedMultilingualV3      => "embed-multilingual-v3.0",
        EmbeddingModel.CohereEmbedEnglishLightV3      => "embed-english-light-v3.0",
        EmbeddingModel.CohereEmbedMultilingualLightV3 => "embed-multilingual-light-v3.0",

        EmbeddingModel.GoogleTextEmbedding004         => "text-embedding-004",
        EmbeddingModel.GoogleGeminiEmbedding          => "gemini-embedding-exp-03-07",

        EmbeddingModel.MistralEmbed                   => "mistral-embed",

        EmbeddingModel.VoyageV3                       => "voyage-3",
        EmbeddingModel.VoyageV3Lite                   => "voyage-3-lite",
        EmbeddingModel.VoyageLarge2                   => "voyage-large-2",

        EmbeddingModel.NomicEmbedText                 => "nomic-embed-text",
        EmbeddingModel.AllMiniLmL6V2                  => "all-MiniLM-L6-v2",
        EmbeddingModel.BgeLargeEnV15                  => "bge-large-en-v1.5",
        EmbeddingModel.BgeM3                          => "bge-m3",
        EmbeddingModel.MxbaiEmbedLarge                => "mxbai-embed-large",

        EmbeddingModel.Custom                         => "custom",

        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown embedding model.")
    };
}
