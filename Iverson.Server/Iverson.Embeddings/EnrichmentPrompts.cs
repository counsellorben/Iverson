namespace Iverson.Embeddings;

public static class EnrichmentPrompts
{
    public const string Summary =
        "Summarize the following text in 2-3 concise sentences:\n\n{0}";

    public const string Keywords =
        "Extract the 5-10 most important keywords or key phrases from the following text. " +
        "Return them as a comma-separated list:\n\n{0}";

    public const string Extraction =
        "Extract structured information from the following text and return it as JSON:\n\n{0}";

    public const string ChunkContext =
        "Given the following document excerpt, write a brief 1-2 sentence description of what " +
        "this excerpt is about and how it relates to the surrounding document:\n\n{0}";
}
