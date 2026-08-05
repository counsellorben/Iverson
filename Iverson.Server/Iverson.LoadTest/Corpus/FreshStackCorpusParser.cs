using System.Text.Json;

namespace Iverson.LoadTest.Corpus;

/// <summary>
/// Parses FreshStack-format corpus files.
///
/// UNRESOLVED: FreshStack's on-disk layout was not available on the machine
/// this parser was written on (no BEIR or FreshStack data is downloaded in
/// this repo/environment). The exact field names and file formats below are
/// NOT verified against the real dataset — they are provisional, modeled on
/// BEIR's JSONL/TSV shape only to keep the public method shape identical
/// (per the task brief, so Tasks 3 and 4 can treat both corpora uniformly).
///
/// Before this parser is used against real FreshStack data, whoever picks
/// this up MUST:
///   1. Download a FreshStack dataset sample and inspect its actual file
///      layout (corpus/queries/qrels format, field names, nugget id field).
///   2. Correct ParseCorpus/ParseQueries/ParseQrels below to match reality.
///   3. Write the FreshStack-specific unit test that Step 6 of the task
///      brief calls for (nugget id landing in Qrel.Subtopic), which was
///      deliberately skipped here because there was no verified format to
///      test against.
///
/// The one substantive thing pinned by the design (spec A3) is that the
/// nugget id must land in <see cref="Qrel.Subtopic"/>, not be dropped -
/// that's what makes alpha-nDCG computable downstream. The field name used
/// to source it below ("nugget_id") is a guess and must be verified.
/// </summary>
public static class FreshStackCorpusParser
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

            // UNRESOLVED: assuming "_id"/"title"/"text" like BEIR; unverified.
            var id = root.TryGetProperty("_id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                throw new FormatException($"FreshStack corpus line {lineNumber}: missing or empty \"_id\".");
            }

            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";

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

            // UNRESOLVED: assuming "_id"/"text" like BEIR; unverified.
            var id = root.TryGetProperty("_id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                throw new FormatException($"FreshStack queries line {lineNumber}: missing or empty \"_id\".");
            }

            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";

            queries.Add(new CorpusQuery(id, text));
        }

        return queries;
    }

    public static List<Qrel> ParseQrels(TextReader reader)
    {
        var qrels = new List<Qrel>();
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // UNRESOLVED: assuming one JSON object per line with
            // "query_id", "corpus_id" (or "doc_id"), "nugget_id", "score".
            // This is a guess pending inspection of a real FreshStack qrels
            // file; the field names, and even the JSONL-vs-TSV shape, are
            // unverified.
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var queryId = root.TryGetProperty("query_id", out var qIdProp) ? qIdProp.GetString() : null;
            var docId = root.TryGetProperty("corpus_id", out var dIdProp) ? dIdProp.GetString() : null;
            var nuggetId = root.TryGetProperty("nugget_id", out var nIdProp) ? nIdProp.GetString() : null;

            if (string.IsNullOrEmpty(queryId) || string.IsNullOrEmpty(docId))
            {
                throw new FormatException($"FreshStack qrels line {lineNumber}: missing query_id or corpus_id.");
            }

            var relevance = root.TryGetProperty("score", out var scoreProp) && scoreProp.TryGetInt32(out var score)
                ? score
                : 1;

            qrels.Add(new Qrel(queryId, nuggetId ?? "", docId, relevance));
        }

        return qrels;
    }
}
