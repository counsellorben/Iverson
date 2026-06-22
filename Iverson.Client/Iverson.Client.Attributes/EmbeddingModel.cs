namespace Iverson.Client.Attributes;

public enum EmbeddingModel
{
    // OpenAI
    OpenAiTextEmbedding3Small,      // 1536
    OpenAiTextEmbedding3Large,      // 3072
    OpenAiTextEmbeddingAda002,      // 1536

    // Cohere
    CohereEmbedEnglishV3,           // 1024
    CohereEmbedMultilingualV3,      // 1024
    CohereEmbedEnglishLightV3,      // 384
    CohereEmbedMultilingualLightV3, // 384

    // Google
    GoogleTextEmbedding004,         // 768
    GoogleGeminiEmbedding,          // 3072

    // Mistral
    MistralEmbed,                   // 1024

    // Voyage AI
    VoyageV3,                       // 1024
    VoyageV3Lite,                   // 512
    VoyageLarge2,                   // 1536

    // Open-source / local (Ollama, HuggingFace)
    NomicEmbedText,                 // 768
    AllMiniLmL6V2,                  // 384
    BgeLargeEnV15,                  // 1024
    BgeM3,                          // 1024
    MxbaiEmbedLarge,                // 1024

    // Use with [IversonVector(EmbeddingModel.Custom, dimension: N)]
    Custom                          // caller-supplied
}
