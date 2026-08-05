using FluentAssertions;
using Iverson.LoadTest.Corpus;
using Xunit;

namespace Iverson.LoadTest.Tests.Corpus;

public class BeirCorpusParserTests
{
    [Fact]
    public void ParseCorpus_WellFormedMultiLine_ParsesAllDocuments()
    {
        var input = """
            {"_id": "doc1", "title": "First Title", "text": "First body text."}
            {"_id": "doc2", "title": "Second Title", "text": "Second body text."}
            """;

        var result = BeirCorpusParser.ParseCorpus(new StringReader(input));

        result.Should().BeEquivalentTo(new[]
        {
            new CorpusDocument("doc1", "First Title", "First body text."),
            new CorpusDocument("doc2", "Second Title", "Second body text."),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void ParseCorpus_DocumentWithNoTitle_TitleBecomesEmptyString()
    {
        var input = """{"_id": "doc1", "text": "Body text only."}""";

        var result = BeirCorpusParser.ParseCorpus(new StringReader(input));

        result.Should().ContainSingle().Which.Should().Be(new CorpusDocument("doc1", "", "Body text only."));
    }

    [Fact]
    public void ParseCorpus_MissingId_ThrowsWithLineNumber()
    {
        var input = """
            {"_id": "doc1", "title": "T", "text": "X"}
            {"title": "No id here", "text": "Y"}
            """;

        var act = () => BeirCorpusParser.ParseCorpus(new StringReader(input));

        act.Should().Throw<FormatException>().WithMessage("*line 2*");
    }

    [Fact]
    public void ParseCorpus_EmptyId_ThrowsWithLineNumber()
    {
        var input = """{"_id": "", "title": "T", "text": "X"}""";

        var act = () => BeirCorpusParser.ParseCorpus(new StringReader(input));

        act.Should().Throw<FormatException>().WithMessage("*line 1*");
    }

    [Fact]
    public void ParseQueries_WellFormedMultiLine_ParsesAllQueries()
    {
        var input = """
            {"_id": "q1", "text": "What is the capital of France?"}
            {"_id": "q2", "text": "How does photosynthesis work?"}
            """;

        var result = BeirCorpusParser.ParseQueries(new StringReader(input));

        result.Should().BeEquivalentTo(new[]
        {
            new CorpusQuery("q1", "What is the capital of France?"),
            new CorpusQuery("q2", "How does photosynthesis work?"),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void ParseQrels_HeaderIsSkipped_AndSubtopicIsZero()
    {
        var input = "query-id\tcorpus-id\tscore\n" +
                     "q1\tdoc1\t1\n" +
                     "q1\tdoc2\t0\n" +
                     "q2\tdoc3\t2\n";

        var result = BeirCorpusParser.ParseQrels(new StringReader(input));

        result.Should().BeEquivalentTo(new[]
        {
            new Qrel("q1", "0", "doc1", 1),
            new Qrel("q1", "0", "doc2", 0),
            new Qrel("q2", "0", "doc3", 2),
        }, options => options.WithStrictOrdering());
    }
}
