namespace Iverson.LoadTest.Corpus;

public sealed record CorpusDocument(string DocId, string Title, string Text);
public sealed record CorpusQuery(string QueryId, string Text);
/// <param name="Subtopic">TREC qrels iteration field. BEIR writes "0"; FreshStack carries the nugget id here.</param>
public sealed record Qrel(string QueryId, string Subtopic, string DocId, int Relevance);
