using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using Xunit;
using static Iverson.Client.Search.SearchOperators;

namespace Iverson.Client.Search.Tests;

public class PipelineBuilderTests
{
    [Fact]
    public void Build_FullPipeline_CompilesToExpectedProto()
    {
        var request = Query.Pipeline("Article")
            .Where("IsPublished", EqualTo, true)
            .Step("by_author", s => s
                .GroupBy("AuthorId")
                .CountAll("articles")
                .Having("articles", GreaterThan, 5))
            .Step("ranked", s => s
                .RowNumber("rank", orderBy: "articles", descending: true))
            .Step("named", s => s
                .Join("Author", ("AuthorId", "Id"))
                .Select(p => p.AllFrom("ranked").Pick("Author", "Name", "author_name")))
            .SortOn("rank")
            .Limit(5)
            .Build();

        request.TypeName.Should().Be("Article");
        request.BaseWhere.Should().HaveCount(1);
        request.BaseWhere[0].Property.Should().Be("IsPublished");
        request.Steps.Should().HaveCount(3);

        var agg = request.Steps[0];
        agg.Name.Should().Be("by_author");
        agg.GroupBy.Should().ContainSingle(k => k.Field == "AuthorId" && k.DateTrunc == DateTrunc.None);
        agg.Metrics.Should().ContainSingle(m => m.Name == "articles" && m.Type == AggregationType.Count);
        agg.Having.Should().ContainSingle(h => h.Property == "articles");

        var win = request.Steps[1];
        win.Windows.Should().ContainSingle(w =>
            w.Alias == "rank" && w.Kind == WindowFunctionKind.RowNumber &&
            w.OrderBy == "articles" && w.Descending);

        var joined = request.Steps[2];
        joined.Joins.Should().ContainSingle(j => j.Source == "Author" && j.Kind == JoinKind.Inner);
        joined.Joins[0].On.Should().ContainSingle(c => c.Left == "AuthorId" && c.Right == "Id");
        joined.Select.Should().HaveCount(2);
        joined.Select[0].All.Should().BeTrue();
        joined.Select[0].Source.Should().Be("ranked");
        joined.Select[1].Alias.Should().Be("author_name");

        request.OrderBy.Should().ContainSingle(o => o.Property == "rank");
        request.Limit.Should().Be(5);
    }

    [Fact]
    public void Build_Typed_UsesTypeName()
    {
        var request = Query.Pipeline<PipelineBuilderTests>().Build();
        request.TypeName.Should().Be(nameof(PipelineBuilderTests));
        request.Limit.Should().Be(10_000);
    }

    [Fact]
    public void Step_ExplicitReads_IsCarried()
    {
        var request = Query.Pipeline("Article")
            .Step("a", s => s.Derive("x", "WordCount + 1"))
            .Step("b", s => s.Reads("base").Derive("y", "WordCount + 2"))
            .Build();

        request.Steps[1].Reads.Should().Be("base");
    }

    [Fact]
    public void Step_DuplicateName_Throws()
    {
        var act = () => Query.Pipeline("Article")
            .Step("x", s => s.Derive("a", "WordCount"))
            .Step("X", s => s.Derive("b", "WordCount"));
        act.Should().Throw<ArgumentException>().WithMessage("*X*");
    }

    [Fact]
    public void Step_ReadsUnknownStep_Throws()
    {
        var act = () => Query.Pipeline("Article")
            .Step("a", s => s.Reads("nope").Derive("x", "WordCount"));
        act.Should().Throw<ArgumentException>().WithMessage("*nope*");
    }

    [Fact]
    public void Step_WindowAndGroupBy_Throws()
    {
        var act = () => Query.Pipeline("Article")
            .Step("bad", s => s
                .RowNumber("rn", orderBy: "Id")
                .GroupBy("AuthorId").CountAll());
        act.Should().Throw<ArgumentException>().WithMessage("*bad*");
    }

    [Fact]
    public void Step_MetricsWithoutGroupBy_Throws()
    {
        var act = () => Query.Pipeline("Article")
            .Step("bad", s => s.CountAll("n"));
        act.Should().Throw<ArgumentException>().WithMessage("*bad*");
    }

    [Fact]
    public void Step_JoinWithoutSelect_Throws()
    {
        var act = () => Query.Pipeline("Article")
            .Step("bad", s => s.Join("Author", ("AuthorId", "Id")));
        act.Should().Throw<ArgumentException>().WithMessage("*select*");
    }

    [Fact]
    public void Step_DuplicateAliases_Throws()
    {
        var act = () => Query.Pipeline("Article")
            .Step("bad", s => s
                .RowNumber("x", orderBy: "Id")
                .Derive("X", "WordCount + 1"));
        act.Should().Throw<ArgumentException>().WithMessage("*X*");
    }

    [Fact]
    public void Windows_AllKinds_MapToProtoKinds()
    {
        var request = Query.Pipeline("Article")
            .Step("w", s => s
                .RowNumber("a", orderBy: "Id")
                .Rank("b", orderBy: "Id")
                .DenseRank("c", orderBy: "Id")
                .RunningSum("d", "WordCount", orderBy: "Id")
                .RunningAvg("e", "WordCount", orderBy: "Id")
                .Lag("f", "WordCount", orderBy: "Id", offset: 2)
                .Lead("g", "WordCount", orderBy: "Id"))
            .Build();

        var kinds = request.Steps[0].Windows.Select(w => w.Kind).ToList();
        kinds.Should().Equal(
            WindowFunctionKind.RowNumber, WindowFunctionKind.Rank, WindowFunctionKind.DenseRank,
            WindowFunctionKind.RunningSum, WindowFunctionKind.RunningAvg,
            WindowFunctionKind.Lag, WindowFunctionKind.Lead);
        request.Steps[0].Windows[5].Offset.Should().Be(2);
        request.Steps[0].Windows[6].Offset.Should().Be(1);
    }

    [Fact]
    public void GroupBy_WithDateTrunc_SetsEnum()
    {
        var request = Query.Pipeline("Article")
            .Step("m", s => s.GroupBy("PublishedAt", DateTrunc.Month).CountAll("n"))
            .Build();

        request.Steps[0].GroupBy[0].DateTrunc.Should().Be(DateTrunc.Month);
    }

    // ── Cross-language golden-fixture contract ─────────────────────────────────
    // Golden fixture generated from this exact builder invocation, checked in at
    // Iverson.Clients/Common/testdata/pipeline-contract-1.json. Java, Python,
    // TypeScript, and Go each have an equivalent test asserting their builder
    // produces the same structural JSON when built with the same inputs.
    //
    // Covers a base-step filter, an aggregate step (GroupBy/CountAll/Having), and a
    // join step using a composite-key join (2 ON pairs) plus a select projection —
    // the exact shape that Tasks 15-18 of the 2026-07-06 vector-search-and-dsl-followups
    // plan had to equalize across all 5 languages after real per-language divergences.
    //
    // If a legitimate proto/DSL change requires updating this fixture, regenerate it
    // from this C# builder invocation (the reference implementation) — do not hand-edit
    // the JSON file.

    [Fact]
    public void Build_MatchesGoldenFixture_PipelineContract1()
    {
        var request = Query.Pipeline("Article")
            .Where("IsPublished", EqualTo, true)
            .Step("by_author", s => s
                .GroupBy("AuthorId")
                .CountAll("articles")
                .Having("articles", GreaterThan, 5))
            .Step("enriched", s => s
                .Join("Author", [("AuthorId", "Id"), ("TenantId", "TenantId")])
                .Select(p => p.AllFrom("by_author").Pick("Author", "Name", "author_name")))
            .SortOn("articles", descending: true)
            .Limit(25)
            .Build("fixture-trace-id");

        var actualJson = Google.Protobuf.JsonFormatter.Default.Format(request);
        var actual = System.Text.Json.JsonDocument.Parse(actualJson).RootElement;

        var goldenPath = Path.Combine(AppContext.BaseDirectory, "testdata", "pipeline-contract-1.json");
        var expected = System.Text.Json.JsonDocument.Parse(File.ReadAllText(goldenPath)).RootElement;

        // Re-serialize both to a canonical compact form before comparing: JsonElement.GetRawText()
        // preserves the source's original whitespace, so a pretty-printed golden file would never
        // raw-text-equal a compactly-formatted actual value even when structurally identical.
        System.Text.Json.JsonSerializer.Serialize(actual).Should().Be(System.Text.Json.JsonSerializer.Serialize(expected));
    }
}
