using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class RerankInputsTests
{
    private static readonly IReadOnlyList<(string DocId, double Score, string Text)> Winners =
    [
        ("doc-a", 0.9, "chunk of a"),
        ("doc-b", 0.5, "chunk of b"),
    ];

    // Mutation: returning WinningChunk for "document" (or the default) would pass a no-op Parse.
    [Fact]
    public void Parse_Document_WithUrl_IsDocument()
    {
        RerankInputs.Parse("document", "http://127.0.0.1:8090").Should().Be(RerankInput.Document);
        RerankInputs.Parse("winning-chunk", "").Should().Be(RerankInput.WinningChunk);
    }

    // Mutation: a `_ => RerankInput.WinningChunk` default arm would silently run a mislabelled arm.
    [Fact]
    public void Parse_UnknownValue_Throws_NamingTheValue()
    {
        var act = () => RerankInputs.Parse("title-chunk", "http://127.0.0.1:8090");

        act.Should().Throw<InvalidOperationException>().WithMessage("*title-chunk*");
    }

    // Mutation: dropping the url check lets an input mode be declared for a reranker that is not there.
    [Fact]
    public void Parse_DocumentWithoutUrl_Throws()
    {
        var act = () => RerankInputs.Parse("document", "");

        act.Should().Throw<InvalidOperationException>().WithMessage("*--rerank-url*");
    }

    // Mutation: any reordering, or reading the corpus map in this mode, breaks the A1 semantics.
    [Fact]
    public void Select_WinningChunk_ReturnsChunkTextsInRankedOrder()
    {
        var texts = RerankInputs.Select(RerankInput.WinningChunk, Winners, corpusText: null);

        texts.Should().Equal("chunk of a", "chunk of b");
    }

    // Mutation: returning the chunk text, or iterating the dictionary instead of the winners, fails.
    [Fact]
    public void Select_Document_ReturnsCorpusTextsInRankedOrder()
    {
        var corpus = new Dictionary<string, string>
        {
            ["doc-b"] = "Title B. Abstract of b",
            ["doc-a"] = "Title A. Abstract of a",
        };

        var texts = RerankInputs.Select(RerankInput.Document, Winners, corpus);

        texts.Should().Equal("Title A. Abstract of a", "Title B. Abstract of b");
    }

    // Mutation: a TryGetValue fallback to "" or to the chunk text would be scored silently.
    [Fact]
    public void Select_Document_MissingDocId_Throws_NamingTheDocId()
    {
        var corpus = new Dictionary<string, string> { ["doc-a"] = "Title A. Abstract of a" };

        var act = () => RerankInputs.Select(RerankInput.Document, Winners, corpus);

        act.Should().Throw<InvalidOperationException>().WithMessage("*doc-b*");
    }
}
