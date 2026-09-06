using System.Text.Json;

namespace Iverson.LoadTest.Corpus;

/// <summary>
/// Parses JSONL corpus and query files: one JSON object per line, with "_id", "title", "text" in
/// corpus.jsonl and "_id", "text" in queries.jsonl. This is BEIR's on-disk shape
/// (https://github.com/beir-cellar/beir); FreshStack-derived files are normalised into the same
/// shape upstream of this parser before reaching it.
/// </summary>
public static class JsonlCorpusParser
{
    public static List<CorpusDocument> ParseCorpus(TextReader reader)
    {
        var documents = new List<CorpusDocument>();
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var id = root.TryGetProperty("_id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                throw new FormatException($"Corpus line {lineNumber}: missing or empty \"_id\".");
            }

            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";

            // Fail as loudly as a missing "_id" does. A document with empty body text persists fine
            // and counts as a success, but IntelligenceStoreConsumer skips whitespace-only text on
            // both the vector and the chunk path — so it is invisible to every query. A body field
            // that is actually named something else would otherwise produce a full corpus of these
            // and an entirely empty run file, with success reported at every checkpoint.
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FormatException(
                    $"Corpus line {lineNumber} (_id \"{id}\"): missing or empty \"text\". A document " +
                    "with no body is never indexed and would silently vanish from every result set.");
            }

            documents.Add(new CorpusDocument(id, title, text));
        }

        return documents;
    }

    public static List<CorpusQuery> ParseQueries(TextReader reader)
    {
        var queries = new List<CorpusQuery>();
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var id = root.TryGetProperty("_id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                throw new FormatException($"Queries line {lineNumber}: missing or empty \"_id\".");
            }

            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";

            // Mirrors the ParseCorpus guard above, for the same reason on the query side: an empty query
            // string is embedded and searched without error, returns a meaningless ranking, and produces a
            // run file that scores zero relevance with success reported at every checkpoint.
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FormatException(
                    $"Queries line {lineNumber} (_id \"{id}\"): missing or empty \"text\". A query with no " +
                    "text is embedded as an empty vector and silently scores nothing.");
            }

            queries.Add(new CorpusQuery(id, text));
        }

        return queries;
    }
}
