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

    // Two slots: {0} is the surrounding document's context (its generated summary when one
    // exists, otherwise a truncated slice of the source text), {1} is the excerpt itself.
    // The result is prepended to the excerpt before embedding, so it must be the description
    // and nothing else.
    public const string ChunkContext =
        "Here is the context of a document:\n\n{0}\n\n" +
        "Here is an excerpt from that same document:\n\n{1}\n\n" +
        "Write a brief 1-2 sentence description of what this excerpt is about and how it " +
        "relates to the surrounding document. Respond with the description only.";
}
