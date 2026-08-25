namespace Iverson.LoadTest.Corpus;

public sealed record CorpusDocument(string DocId, string Title, string Text);
public sealed record CorpusQuery(string QueryId, string Text);
