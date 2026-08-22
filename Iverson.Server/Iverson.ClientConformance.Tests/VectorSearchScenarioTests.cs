using System.Text.Json;
using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for S7's judgement, which is pure over reported data
/// (<see cref="VectorSearchScenario.Judge"/>, <see cref="VectorSearchScenario.ReadLabels"/>,
/// <see cref="VectorSearchScenario.ReadParentKeys"/>, <see cref="VectorSearchScenario.ExpectedKeys"/>,
/// <see cref="VectorSearchScenario.ExpectedLabels"/>) and so is exercisable without a live stack.
///
/// Every test here names the mutation it would catch: an assertion that cannot be made to fail is
/// not evidence, and the whole point of this file is that each VEC requirement's cell goes red for
/// exactly the defect its statement describes and for nothing else.
/// </summary>
public class VectorSearchScenarioTests
{
    private static readonly Guid KeyA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid KeyB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid KeyC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static PhaseDocument ReadDocument(params StepResult[] steps) => new("dotnet", "read", steps);

    private static JsonElement SimilarEntity(params string[] labels) =>
        JsonSerializer.SerializeToElement(new { labels });

    private static JsonElement ChunksEntity(params Guid[] parentKeys) =>
        JsonSerializer.SerializeToElement(new { parentKeys = parentKeys.Select(k => k.ToString()).ToList() });

    private static StepResult SimilarStep(params string[] labels) =>
        new(VectorSearchScenario.SimilarStepName, true, Entity: SimilarEntity(labels));

    private static StepResult ChunksStep(params Guid[] parentKeys) =>
        new(VectorSearchScenario.ChunksStepName, true, Entity: ChunksEntity(parentKeys));

    private static Assertion Cited(IReadOnlyList<Assertion> assertions, string requirementId) =>
        assertions.Single(a => a.RequirementId == requirementId);

    private static Assertion Named(IReadOnlyList<Assertion> assertions, string fragment) =>
        assertions.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal));

    private static HashSet<string> Labels(params string[] languages) =>
        languages.Select(VectorSearchScenario.LabelFor).ToHashSet(StringComparer.Ordinal);

    // ── the happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_BothSearchesReturnTheSeededRows_AllAssertionsPass()
    {
        var assertions = VectorSearchScenario.Judge(
            "dotnet",
            new HashSet<Guid> { KeyA, KeyB },
            Labels("dotnet", "python"),
            ReadDocument(
                SimilarStep(VectorSearchScenario.LabelFor("dotnet"), VectorSearchScenario.LabelFor("python")),
                ChunksStep(KeyA, KeyB)));

        assertions.Should().OnlyContain(a => a.Passed);
        assertions.Should().Contain(a => a.RequirementId == Requirements.VecSimilaritySearchReachable);
        assertions.Should().Contain(a => a.RequirementId == Requirements.VecSimilarityReturnsExactlyFilteredRows);
        assertions.Should().Contain(a => a.RequirementId == Requirements.VecChunkSearchReachable);
        assertions.Should().Contain(a => a.RequirementId == Requirements.VecChunkSearchReturnsExactlyFilteredParents);
    }

    [Fact]
    public void Judge_ResultOrderDiffersFromTheSeedOrder_StillPasses()
    {
        // The comparison is a set comparison on purpose: SearchSimilar's order is a fused,
        // MMR-diversified ranking that no client controls, and no VEC requirement constrains it
        // (ranking is Deferred in the VEC coverage ledger).
        var assertions = VectorSearchScenario.Judge(
            "go",
            new HashSet<Guid> { KeyA, KeyB },
            Labels("dotnet", "python"),
            ReadDocument(
                SimilarStep(VectorSearchScenario.LabelFor("python"), VectorSearchScenario.LabelFor("dotnet")),
                ChunksStep(KeyB, KeyA)));

        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeTrue();
        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_ChunkSearchReturnedSeveralChunksPerParent_StillPasses()
    {
        // IVC-VEC-004 constrains which PARENTS the filter admits, not how many windows the server
        // split their text into — so a repeated parent key must not read as an unexpected row.
        var assertions = VectorSearchScenario.Judge(
            "java",
            new HashSet<Guid> { KeyA, KeyB },
            Labels("dotnet"),
            ReadDocument(
                SimilarStep(VectorSearchScenario.LabelFor("dotnet")),
                ChunksStep(KeyA, KeyA, KeyB, KeyB, KeyB)));

        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents).Passed.Should().BeTrue();
    }

    // ── IVC-VEC-002: the exact similarity result set, both directions ─────────────────────────

    [Fact]
    public void Judge_SimilaritySearchDroppedASeededRow_FailsOnlyTheSimilarityResultSet()
    {
        // The mutation this pins: a client whose filter clause is built wrongly (or whose topK
        // truncates) returns fewer rows than the run seeded. Reachability still passes — the call
        // completed — and the chunk requirements are untouched.
        var assertions = VectorSearchScenario.Judge(
            "python",
            new HashSet<Guid> { KeyA, KeyB },
            Labels("dotnet", "python"),
            ReadDocument(
                SimilarStep(VectorSearchScenario.LabelFor("dotnet")),
                ChunksStep(KeyA, KeyB)));

        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeFalse();
        Cited(assertions, Requirements.VecSimilaritySearchReachable).Passed.Should().BeTrue();
        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents).Passed.Should().BeTrue();
        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows)
            .Detail.Should().Contain(VectorSearchScenario.LabelFor("python"));
    }

    [Fact]
    public void Judge_SimilaritySearchReturnedARowTheRunNeverSeeded_FailsTheSimilarityResultSet()
    {
        // The other direction: a client that dropped the run-marker filter entirely would see an
        // earlier run's rows. A one-way subset check would wave that through.
        var assertions = VectorSearchScenario.Judge(
            "typescript",
            new HashSet<Guid> { KeyA },
            Labels("dotnet"),
            ReadDocument(
                SimilarStep(VectorSearchScenario.LabelFor("dotnet"), "vec-someone-else"),
                ChunksStep(KeyA)));

        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeFalse();
        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows)
            .Detail.Should().Contain("vec-someone-else");
    }

    [Fact]
    public void Judge_SimilarityStepFailed_FailsBothReachabilityAndTheResultSet()
    {
        var assertions = VectorSearchScenario.Judge(
            "go",
            new HashSet<Guid> { KeyA },
            Labels("dotnet"),
            ReadDocument(
                new StepResult(VectorSearchScenario.SimilarStepName, false, "Unavailable: embedding service down"),
                ChunksStep(KeyA)));

        Cited(assertions, Requirements.VecSimilaritySearchReachable).Passed.Should().BeFalse();
        Cited(assertions, Requirements.VecSimilaritySearchReachable).Detail.Should().Contain("embedding service down");
        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_SimilarityStepAbsent_FailsReachabilityNamingTheMissingStep()
    {
        var assertions = VectorSearchScenario.Judge(
            "java", new HashSet<Guid> { KeyA }, Labels("dotnet"), ReadDocument(ChunksStep(KeyA)));

        Cited(assertions, Requirements.VecSimilaritySearchReachable).Passed.Should().BeFalse();
        Cited(assertions, Requirements.VecSimilaritySearchReachable)
            .Detail.Should().Contain(VectorSearchScenario.SimilarStepName);
        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_SimilaritySearchStreamedRowsButBoundNoneOfTheirFields_IsDistinguishableFromAnEmptyResult()
    {
        // The live failure this pins: a client whose typed projection binds PascalCase payload keys
        // gets nothing out of SearchSimilar's camelCase payload, so it reports one EMPTY label per
        // row. Joined into a list that renders exactly like "returned nothing", which has a wholly
        // different cause — so the detail must state the label count.
        var boundNothing = VectorSearchScenario.Judge(
            "go",
            new HashSet<Guid> { KeyA },
            Labels("dotnet"),
            ReadDocument(SimilarStep("", "", ""), ChunksStep(KeyA)));

        var returnedNothing = VectorSearchScenario.Judge(
            "go", new HashSet<Guid> { KeyA }, Labels("dotnet"), ReadDocument(SimilarStep(), ChunksStep(KeyA)));

        Cited(boundNothing, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeFalse();
        Cited(returnedNothing, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeFalse();

        Cited(boundNothing, Requirements.VecSimilarityReturnsExactlyFilteredRows)
            .Detail.Should().Contain("1 distinct label(s)");
        Cited(returnedNothing, Requirements.VecSimilarityReturnsExactlyFilteredRows)
            .Detail.Should().Contain("0 distinct label(s)");
    }

    // ── IVC-VEC-004: the exact chunk parent set, both directions ──────────────────────────────

    [Fact]
    public void Judge_ChunkSearchDroppedASeededParent_FailsOnlyTheChunkParentSet()
    {
        var assertions = VectorSearchScenario.Judge(
            "python",
            new HashSet<Guid> { KeyA, KeyB },
            Labels("dotnet"),
            ReadDocument(SimilarStep(VectorSearchScenario.LabelFor("dotnet")), ChunksStep(KeyA)));

        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents).Passed.Should().BeFalse();
        Cited(assertions, Requirements.VecChunkSearchReachable).Passed.Should().BeTrue();
        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeTrue();
        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents)
            .Detail.Should().Contain(KeyB.ToString());
    }

    [Fact]
    public void Judge_ChunkSearchReturnedAParentTheRunNeverSeeded_FailsTheChunkParentSet()
    {
        var assertions = VectorSearchScenario.Judge(
            "dotnet",
            new HashSet<Guid> { KeyA },
            Labels("dotnet"),
            ReadDocument(SimilarStep(VectorSearchScenario.LabelFor("dotnet")), ChunksStep(KeyA, KeyC)));

        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents).Passed.Should().BeFalse();
        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents)
            .Detail.Should().Contain(KeyC.ToString());
    }

    [Fact]
    public void Judge_ChunkStepFailed_FailsReachabilityAndTheParentSet()
    {
        var assertions = VectorSearchScenario.Judge(
            "typescript",
            new HashSet<Guid> { KeyA },
            Labels("dotnet"),
            ReadDocument(
                SimilarStep(VectorSearchScenario.LabelFor("dotnet")),
                new StepResult(VectorSearchScenario.ChunksStepName, false, "InvalidArgument: no [IversonChunk]")));

        Cited(assertions, Requirements.VecChunkSearchReachable).Passed.Should().BeFalse();
        Cited(assertions, Requirements.VecChunkSearchReachable).Detail.Should().Contain("IversonChunk");
        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_ChunkStepAbsent_FailsBothChunkRequirements()
    {
        var assertions = VectorSearchScenario.Judge(
            "go",
            new HashSet<Guid> { KeyA },
            Labels("dotnet"),
            ReadDocument(SimilarStep(VectorSearchScenario.LabelFor("dotnet"))));

        Cited(assertions, Requirements.VecChunkSearchReachable).Passed.Should().BeFalse();
        Cited(assertions, Requirements.VecChunkSearchReachable)
            .Detail.Should().Contain(VectorSearchScenario.ChunksStepName);
        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents).Passed.Should().BeFalse();
    }

    // ── the VEC backstop (uncited by design) ─────────────────────────────────────────────────

    [Fact]
    public void Judge_NothingWasSeeded_FailsTheBackstopEvenThoughBothSetComparisonsAgree()
    {
        // Every write denied: both expected sets are empty, an empty similarity result and an
        // empty chunk result compare equal to them, and the cell would be green on nothing.
        var assertions = VectorSearchScenario.Judge(
            "dotnet",
            new HashSet<Guid>(),
            new HashSet<string>(),
            ReadDocument(SimilarStep(), ChunksStep()));

        Cited(assertions, Requirements.VecSimilarityReturnsExactlyFilteredRows).Passed.Should().BeTrue();
        Cited(assertions, Requirements.VecChunkSearchReturnsExactlyFilteredParents).Passed.Should().BeTrue();

        var backstop = Named(assertions, "seeded at least one row");
        backstop.Passed.Should().BeFalse();
        backstop.RequirementId.Should().BeNull();
    }

    [Fact]
    public void Judge_BackstopFiresOnEveryPathIncludingAnEmptyDocument()
    {
        var assertions = VectorSearchScenario.Judge(
            "java", new HashSet<Guid>(), new HashSet<string>(), ReadDocument());

        Named(assertions, "seeded at least one row").Passed.Should().BeFalse();
    }

    // ── the reporting readers ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadLabels_MalformedOrAbsentDocument_YieldsNoLabelsRatherThanThrowing()
    {
        VectorSearchScenario.ReadLabels(null).Should().BeEmpty();
        VectorSearchScenario.ReadLabels(JsonSerializer.SerializeToElement("not an object")).Should().BeEmpty();
        VectorSearchScenario.ReadLabels(JsonSerializer.SerializeToElement(new { labels = 7 })).Should().BeEmpty();
        VectorSearchScenario.ReadLabels(JsonSerializer.SerializeToElement(new { other = new[] { "x" } }))
            .Should().BeEmpty();
    }

    [Fact]
    public void ReadLabels_IgnoresNonStringEntriesButKeepsTheStrings()
    {
        var entity = JsonSerializer.SerializeToElement(new { labels = new object?[] { "vec-go", 7, null, "vec-java" } });

        VectorSearchScenario.ReadLabels(entity).Should().BeEquivalentTo(["vec-go", "vec-java"]);
    }

    [Fact]
    public void ReadParentKeys_ParsesUuidsIrrespectiveOfSpellingAndDeduplicates()
    {
        var entity = JsonSerializer.SerializeToElement(new
        {
            parentKeys = new[]
            {
                KeyA.ToString().ToUpperInvariant(),
                KeyA.ToString("B"),
                "not-a-uuid",
                KeyB.ToString(),
            },
        });

        VectorSearchScenario.ReadParentKeys(entity).Should().BeEquivalentTo([KeyA, KeyB]);
    }

    [Fact]
    public void ReadParentKeys_MalformedOrAbsentDocument_YieldsNoKeysRatherThanThrowing()
    {
        VectorSearchScenario.ReadParentKeys(null).Should().BeEmpty();
        VectorSearchScenario.ReadParentKeys(JsonSerializer.SerializeToElement(new { parentKeys = "nope" }))
            .Should().BeEmpty();
    }

    // ── the harness's own expectation ────────────────────────────────────────────────────────

    [Fact]
    public void ExpectedKeysAndLabels_AreDerivedFromTheLanguagesThatActuallyReportedARowKey()
    {
        var keys = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["dotnet"] = new Dictionary<string, string> { [VectorSearchScenario.RowKeyName] = KeyA.ToString() },
            ["go"] = new Dictionary<string, string> { [VectorSearchScenario.RowKeyName] = KeyB.ToString() },
            // Reported a key under a DIFFERENT scenario's logical name only — this language seeded
            // no VectorDoc row and must not be counted in either expectation.
            ["java"] = new Dictionary<string, string> { ["query_doc"] = KeyC.ToString() },
            // Reported an unparsable key: the same, and it must not throw.
            ["python"] = new Dictionary<string, string> { [VectorSearchScenario.RowKeyName] = "not-a-uuid" },
        };

        VectorSearchScenario.ExpectedKeys(keys).Should().BeEquivalentTo([KeyA, KeyB]);
        VectorSearchScenario.ExpectedLabels(keys).Should().BeEquivalentTo(["vec-dotnet", "vec-go"]);
    }

    [Fact]
    public void LabelFor_IsThePerLanguageIdentityTheDriversStamp()
    {
        VectorSearchScenario.LabelFor("typescript").Should().Be("vec-typescript");
    }

    // ── the projection-wait predicate ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 5)]
    [InlineData(4, 5, 5)]
    [InlineData(5, 4, 5)]
    public void ProjectionReady_EitherCollectionStillBehind_IsNotReady(int similar, int chunks, int expected)
    {
        // Both collections are written by the same consumer but by separate upserts; a wait
        // satisfied on the object collection alone would let the chunk read race the chunk upsert.
        VectorSearchScenario.ProjectionReady(similar, chunks, expected).Should().BeFalse();
    }

    [Theory]
    [InlineData(5, 5, 5)]
    [InlineData(6, 7, 5)]
    public void ProjectionReady_BothCollectionsAtLeastAsFullAsWritten_IsReady(int similar, int chunks, int expected)
    {
        VectorSearchScenario.ProjectionReady(similar, chunks, expected).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 3, 0)]
    public void ProjectionReady_NothingWasWritten_IsDeliberatelyNotReady(int similar, int chunks, int expected)
    {
        // Zero expected is NOT ready on purpose: no language seeded anything, so satisfying the
        // wait would let the read phase grade five clients against nothing.
        VectorSearchScenario.ProjectionReady(similar, chunks, expected).Should().BeFalse();
    }
}
