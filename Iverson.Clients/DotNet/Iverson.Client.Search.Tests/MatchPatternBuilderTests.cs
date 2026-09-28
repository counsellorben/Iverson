using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using Xunit;
using static Iverson.Client.Search.SearchOperators;

namespace Iverson.Client.Search.Tests;

public class MatchPatternBuilderTests
{
    // ── Cross-language golden-fixture contract ─────────────────────────────────
    // Golden fixtures checked in at Iverson.Clients/Common/testdata/match-pattern-contract-{1,2}.json.
    // Java, Python, TypeScript and Go each assert that their builder produces the same structural
    // JSON from the same call sequence. Contract 1 sets every TYPE_ROWS field; contract 2 covers the
    // CHUNKS source and a request with no trace id. Do not hand-edit the JSON to make one SDK pass.

    [Fact]
    public void Build_MatchesGoldenFixture_MatchPatternContract1()
    {
        var request = Query.MatchPattern("PatternDoc")
            .Where("Marker", EqualTo, "m1")
            .Not("Label", EqualTo, "skip")
            .WithLogic(SearchLogic.Or)
            .PartitionBy("Label")
            .OrderBy("Seq")
            .OrderBy("Id", descending: true)
            .Pattern("A B+")
            .Subset("AB", "A", "B")
            .Define("B", "Seq > PREV(Seq)")
            .Measure("n", "COUNT(*)")
            .Measure("last_seq", "LAST(B.Seq)")
            .RowsPerMatch(RowsPerMatch.AllRowsShowEmpty)
            .AfterMatch(AfterMatchSkipKind.ToFirst, "B")
            .Limit(100)
            .Build("trace-1");

        AssertMatchesGolden(request, "match-pattern-contract-1.json");
    }

    [Fact]
    public void Build_MatchesGoldenFixture_MatchPatternContract2()
    {
        var request = Query.MatchPattern("VectorDoc")
            .Chunks("Body")
            .Pattern("A")
            .Define("A", "SIMILARITY(text, 'refund') > 0.5")
            .Build("");

        AssertMatchesGolden(request, "match-pattern-contract-2.json");
    }

    [Fact]
    public void Chunks_SetsSourceAndChunkProperty()
    {
        var request = Query.MatchPattern("VectorDoc").Chunks("Body").Build();

        request.Source.Should().Be(PatternRowSource.Chunks);
        request.ChunkProperty.Should().Be("Body");
    }

    [Fact]
    public void AfterMatch_IsUnsetUnlessCalled()
    {
        Query.MatchPattern("PatternDoc").Pattern("A").Build().AfterMatch.Should().BeNull();

        var request = Query.MatchPattern("PatternDoc").AfterMatch(AfterMatchSkipKind.ToNextRow).Build();
        request.AfterMatch.Should().NotBeNull();
        request.AfterMatch.Kind.Should().Be(AfterMatchSkipKind.ToNextRow);
        request.AfterMatch.Variable.Should().BeEmpty();
    }

    [Fact]
    public void Limit_DefaultsToZero()
    {
        Query.MatchPattern("PatternDoc").Build().Limit.Should().Be(0);
    }

    [Fact]
    public void Build_WithoutTraceId_GivesEmptyTraceId()
    {
        Query.MatchPattern("PatternDoc").Build().TraceId.Should().BeEmpty();
    }

    [Fact]
    public void Build_Typed_UsesTypeName()
    {
        Query.MatchPattern<MatchPatternBuilderTests>().Build().TypeName.Should().Be(nameof(MatchPatternBuilderTests));
    }

    private static void AssertMatchesGolden(MatchPatternRequest request, string fixture)
    {
        var actualJson = Google.Protobuf.JsonFormatter.Default.Format(request);
        var actual = System.Text.Json.JsonDocument.Parse(actualJson).RootElement;

        var goldenPath = Path.Combine(AppContext.BaseDirectory, "testdata", fixture);
        var expected = System.Text.Json.JsonDocument.Parse(File.ReadAllText(goldenPath)).RootElement;

        // Re-serialize both to a canonical compact form, exactly as PipelineBuilderTests does:
        // GetRawText() keeps the golden file's indentation.
        System.Text.Json.JsonSerializer.Serialize(actual).Should().Be(System.Text.Json.JsonSerializer.Serialize(expected));
    }
}
