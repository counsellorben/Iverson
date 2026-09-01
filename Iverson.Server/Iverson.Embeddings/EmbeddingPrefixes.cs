namespace Iverson.Embeddings;

/// <summary>
/// Task prefixes are model-specific. Running snowflake-arctic-embed under nomic's prefixes measured
/// 0.2236 nDCG@10 on NFCorpus against 0.3304 with its own — a 32% relative loss from four tokens of
/// misconfiguration, which nothing in code or tests noticed.
///
/// public, not internal: IngestContractTests (in Iverson.Api.Tests) emits this table into the
/// ingest contract, and Iverson.Embeddings grants InternalsVisibleTo to nothing.
/// </summary>
public static class EmbeddingPrefixes
{
    public const string DefaultDocument = "";
    public const string DefaultQuery    = "";

    // Keyed by model FAMILY. Ollama ids carry tags — "snowflake-arctic-embed:s",
    // "nomic-embed-text:latest" — so the family is everything before the first ':'.
    // This same rule is applied Python-side in ingest.py; see Global Constraint 2.
    //
    // StringComparer.Ordinal is load-bearing across the language boundary, not a local style
    // choice. ingest.py resolves the same family through CONTRACT["embedding"]
    // ["documentPrefixes"].get(family), and Python's dict.get() is case-SENSITIVE — so the two
    // sides agree today precisely because this comparer is ordinal. "Fixing" this to
    // OrdinalIgnoreCase alone would be a textbook Constraint 2 divergence: identical contract
    // data, differing resolution. "NOMIC-Embed-Text" would resolve to "search_document: " in C#
    // and to the empty default in Python, the two paths would embed the same corpus differently,
    // and IngestContractTests would stay green throughout — it pins the TABLE, not the lookup.
    // If case-insensitivity is ever wanted, both sides change in the same commit.
    public static readonly IReadOnlyDictionary<string, (string Document, string Query)> Table =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["nomic-embed-text"]        = ("search_document: ", "search_query: "),
            ["snowflake-arctic-embed"] = ("", "Represent this sentence for searching relevant passages: "),
        };

    public static string Family(string modelId)
    {
        var colon = modelId.IndexOf(':');
        return colon < 0 ? modelId : modelId[..colon];
    }

    public static (string Document, string Query) For(string modelId) =>
        Table.TryGetValue(Family(modelId), out var pair) ? pair : (DefaultDocument, DefaultQuery);
}
