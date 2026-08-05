using System.Text.Json;

namespace Iverson.LoadTest.Corpus;

/// <summary>
/// Parses BEIR-format corpus files: JSONL corpus/queries and TSV qrels.
/// https://github.com/beir-cellar/beir — corpus.jsonl has one JSON object per
/// line with "_id", "title", "text"; queries.jsonl has "_id", "text"; qrels
/// are TSV with a header row: "query-id\tcorpus-id\tscore".
/// </summary>
public static class BeirCorpusParser
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
                throw new FormatException($"BEIR corpus line {lineNumber}: missing or empty \"_id\".");
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

            var id = root.TryGetProperty("_id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                throw new FormatException($"BEIR queries line {lineNumber}: missing or empty \"_id\".");
            }

            var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";

            queries.Add(new CorpusQuery(id, text));
        }

        return queries;
    }

    public static List<Qrel> ParseQrels(TextReader reader)
    {
        var qrels = new List<Qrel>();

        // First line is the header ("query-id\tcorpus-id\tscore"); skip it.
        var header = reader.ReadLine();
        if (header == null)
        {
            return qrels;
        }

        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                throw new FormatException($"BEIR qrels line {lineNumber}: expected 3 tab-separated fields, got {parts.Length}.");
            }

            var queryId = parts[0];
            var docId = parts[1];
            if (!int.TryParse(parts[2], out var relevance))
            {
                throw new FormatException($"BEIR qrels line {lineNumber}: score \"{parts[2]}\" is not an integer.");
            }

            qrels.Add(new Qrel(queryId, "0", docId, relevance));
        }

        return qrels;
    }
}
