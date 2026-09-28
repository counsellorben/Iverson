using System.Text.Json;
using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for the <c>match-pattern</c> scenario's judgement, which is pure over reported data
/// (<see cref="MatchPatternScenario.Judge"/>, <see cref="MatchPatternScenario.ReadRows"/>,
/// <see cref="MatchPatternScenario.ExpectedRows"/>) and so is exercisable without a live stack.
///
/// Every test here names the mutation it would catch: an assertion that cannot be made to fail is
/// not evidence, and each of <c>IVC-QRY-005</c>..<c>007</c> must go red for exactly the defect its
/// statement describes and for nothing else.
/// </summary>
public class MatchPatternScenarioTests
{
    private static readonly Guid D1 = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid D2 = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid D3 = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid P1 = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid P2 = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid P3 = Guid.Parse("e0000000-0000-0000-0000-000000000003");
    private static readonly Guid Stranger = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <summary>The harness's expectation when dotnet and python each seeded their three rows.</summary>
    private static Dictionary<Guid, string> Seeded() => new()
    {
        [D1] = "dotnet", [D2] = "dotnet", [D3] = "dotnet",
        [P1] = "python", [P2] = "python", [P3] = "python",
    };

    private static PhaseDocument ReadDocument(params StepResult[] steps) => new("dotnet", "read", steps);

    private static JsonElement Rows(params object[] rows) => JsonSerializer.SerializeToElement(new { rows });

    private static object ScalarRow(
        string label, double n = 3, double firstSeq = 1, double lastSeq = 3, double matchNumber = 1) => new
    {
        matchNumber,
        classifier = "",
        data = new Dictionary<string, object?>
        {
            ["Label"] = label, ["n"] = n, ["first_seq"] = firstSeq, ["last_seq"] = lastSeq,
        },
    };

    private static object SimilarityRow(
        Guid id, string label, object? s = null, string classifier = "A", double matchNumber = 1) => new
    {
        matchNumber,
        classifier,
        data = new Dictionary<string, object?>
        {
            ["Id"] = id.ToString(), ["Label"] = label, ["Seq"] = 1, ["s"] = s ?? 0.6000000238418579,
        },
    };

    private static StepResult ScalarStep(params object[] rows) =>
        new(MatchPatternScenario.ScalarStepName, true, Entity: Rows(rows));

    private static StepResult SimilarityStep(params object[] rows) =>
        new(MatchPatternScenario.SimilarityStepName, true, Entity: Rows(rows));

    private static StepResult GoodScalarStep() =>
        ScalarStep(ScalarRow("pat-dotnet"), ScalarRow("pat-python"));

    private static StepResult GoodSimilarityStep() => SimilarityStep(
        SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
        SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python"));

    private static IReadOnlyList<Assertion> JudgeSeeded(params StepResult[] steps) =>
        MatchPatternScenario.Judge("dotnet", Seeded(), ReadDocument(steps));

    private static Assertion Scalar(IReadOnlyList<Assertion> assertions) =>
        assertions.Single(a => a.RequirementId == Requirements.QryMatchPatternReturnsExactlyExpectedMatches);

    private static Assertion Similarity(IReadOnlyList<Assertion> assertions) =>
        assertions.Single(a => a.RequirementId == Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow);

    private static Assertion Reachable(IReadOnlyList<Assertion> assertions, string which) =>
        assertions.Single(a => a.RequirementId == Requirements.QryMatchPatternReachable
                               && a.Name.Contains($"a {which} row pattern match", StringComparison.Ordinal));

    // ── the happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_BothPatternsReturnTheExpectedRows_AllAssertionsPass()
    {
        var assertions = JudgeSeeded(GoodScalarStep(), GoodSimilarityStep());

        assertions.Should().OnlyContain(a => a.Passed);
        assertions.Where(a => a.RequirementId == Requirements.QryMatchPatternReachable).Should().HaveCount(2);
        Scalar(assertions).Passed.Should().BeTrue();
        Similarity(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_RowsArriveInAnotherOrder_StillPasses()
    {
        // Both comparisons are keyed, not positional: the partitions' output order is the server's
        // business and no QRY requirement constrains it.
        var assertions = JudgeSeeded(
            ScalarStep(ScalarRow("pat-python"), ScalarRow("pat-dotnet")),
            SimilarityStep(
                SimilarityRow(P3, "pat-python"), SimilarityRow(D1, "pat-dotnet"), SimilarityRow(P1, "pat-python"),
                SimilarityRow(D3, "pat-dotnet"), SimilarityRow(P2, "pat-python"), SimilarityRow(D2, "pat-dotnet")));

        assertions.Should().OnlyContain(a => a.Passed);
    }

    // ── IVC-QRY-005: reachability, and what a missing or failed step does to the content half ──

    [Fact]
    public void Judge_ScalarStepAbsent_FailsScalarReachabilityAndTheScalarResult_NamingTheStep()
    {
        var assertions = JudgeSeeded(GoodSimilarityStep());

        Reachable(assertions, "scalar").Passed.Should().BeFalse();
        Reachable(assertions, "scalar").Detail.Should().Contain(MatchPatternScenario.ScalarStepName);
        Scalar(assertions).Passed.Should().BeFalse();
        Reachable(assertions, "SIMILARITY").Passed.Should().BeTrue();
        Similarity(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_ScalarStepFailed_FailsScalarReachabilityWithTheDriversErrorAndTheScalarResult()
    {
        var assertions = JudgeSeeded(
            new StepResult(MatchPatternScenario.ScalarStepName, false, "InvalidArgument: unknown column 'Seq'"),
            GoodSimilarityStep());

        Reachable(assertions, "scalar").Passed.Should().BeFalse();
        Reachable(assertions, "scalar").Detail.Should().Contain("unknown column 'Seq'");
        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("failed");
    }

    [Fact]
    public void Judge_SimilarityStepAbsent_FailsSimilarityReachabilityAndTheSimilarityResult_NamingTheStep()
    {
        var assertions = JudgeSeeded(GoodScalarStep());

        Reachable(assertions, "SIMILARITY").Passed.Should().BeFalse();
        Reachable(assertions, "SIMILARITY").Detail.Should().Contain(MatchPatternScenario.SimilarityStepName);
        Similarity(assertions).Passed.Should().BeFalse();
        Reachable(assertions, "scalar").Passed.Should().BeTrue();
        Scalar(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_SimilarityStepFailed_FailsSimilarityReachabilityWithTheDriversErrorAndTheResult()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            new StepResult(MatchPatternScenario.SimilarityStepName, false, "Unavailable: embedding service down"));

        Reachable(assertions, "SIMILARITY").Passed.Should().BeFalse();
        Reachable(assertions, "SIMILARITY").Detail.Should().Contain("embedding service down");
        Similarity(assertions).Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_MalformedRows_FailTheContentAssertionButNotReachability()
    {
        // The call completed, so QRY-005 holds; what came back cannot be graded, so the content
        // half fails naming the malformation rather than reading as "no rows".
        var assertions = JudgeSeeded(
            new StepResult(MatchPatternScenario.ScalarStepName, true,
                Entity: JsonSerializer.SerializeToElement(new { rows = new object[] { new { classifier = "" } } })),
            new StepResult(MatchPatternScenario.SimilarityStepName, true,
                Entity: JsonSerializer.SerializeToElement(new { labels = new[] { "pat-dotnet" } })));

        Reachable(assertions, "scalar").Passed.Should().BeTrue();
        Reachable(assertions, "SIMILARITY").Passed.Should().BeTrue();
        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("malformed").And.Contain("matchNumber");
        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("malformed").And.Contain("'rows'");
    }

    // ── IVC-QRY-006: one exact match per seeded language ──────────────────────────────────────

    [Theory]
    [InlineData("n", 2, 1, 3, 1)]
    [InlineData("first_seq", 3, 2, 3, 1)]
    [InlineData("last_seq", 3, 1, 2, 1)]
    [InlineData("matchNumber", 3, 1, 3, 2)]
    public void Judge_ScalarMatchWithAWrongValue_FailsOnlyTheScalarResult_NamingTheField(
        string field, double n, double firstSeq, double lastSeq, double matchNumber)
    {
        var assertions = JudgeSeeded(
            ScalarStep(ScalarRow("pat-dotnet"), ScalarRow("pat-python", n, firstSeq, lastSeq, matchNumber)),
            GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("pat-python").And.Contain(field);
        Scalar(assertions).Detail.Should().NotContain("pat-dotnet");
        Similarity(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_ScalarMeasureAbsent_FailsTheScalarResult()
    {
        var row = new
        {
            matchNumber = 1,
            classifier = "",
            data = new Dictionary<string, object?> { ["Label"] = "pat-python", ["n"] = 3, ["first_seq"] = 1 },
        };

        var assertions = JudgeSeeded(ScalarStep(ScalarRow("pat-dotnet"), row), GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("last_seq");
    }

    [Fact]
    public void Judge_ScalarResultCarriesALabelNoSeededLanguageWrote_FailsNamingIt()
    {
        // A client that dropped the marker filter would see an earlier run's partitions.
        var assertions = JudgeSeeded(
            ScalarStep(ScalarRow("pat-dotnet"), ScalarRow("pat-python"), ScalarRow("pat-someone-else")),
            GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("pat-someone-else");
    }

    [Fact]
    public void Judge_ScalarResultLacksASeededLanguagesMatch_FailsNamingTheMissingLabel()
    {
        var assertions = JudgeSeeded(ScalarStep(ScalarRow("pat-dotnet")), GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("pat-python").And.Contain("0 match(es)");
    }

    [Fact]
    public void Judge_ScalarResultRepeatsASeededLanguagesMatch_Fails()
    {
        // "Exactly one" per language: a client that re-yields a buffered response must not pass
        // on the strength of the label set alone.
        var assertions = JudgeSeeded(
            ScalarStep(ScalarRow("pat-dotnet"), ScalarRow("pat-python"), ScalarRow("pat-python")),
            GoodSimilarityStep());

        Scalar(assertions).Passed.Should().BeFalse();
        Scalar(assertions).Detail.Should().Contain("2 match(es)");
    }

    // ── IVC-QRY-007: every seeded row scored, under the right label ──────────────────────────

    [Fact]
    public void Judge_SimilarityResultDroppedASeededRow_FailsOnlyTheSimilarityResult()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain(P3.ToString());
        Scalar(assertions).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_SimilarityResultCarriesARowTheRunNeverSeeded_Fails()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python"),
                SimilarityRow(Stranger, "pat-python")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain(Stranger.ToString());
    }

    [Fact]
    public void Judge_SimilarityResultRepeatsASeededRow_Fails()
    {
        // "Exactly the seeded rows": a client that re-yields a buffered row must not pass on the
        // strength of the Id set alone.
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python"),
                SimilarityRow(D1, "pat-dotnet")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain(D1.ToString()).And.Contain("more than once");
    }

    [Fact]
    public void Judge_SimilarityRowWithNoUsableId_Fails()
    {
        var noId = new
        {
            matchNumber = 1,
            classifier = "A",
            data = new Dictionary<string, object?> { ["Id"] = "not-a-uuid", ["Label"] = "pat-python", ["s"] = 0.5 },
        };

        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python"),
                noId));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("'Id'");
    }

    [Fact]
    public void Judge_SimilarityRowWithAWrongClassifier_Fails()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"),
                SimilarityRow(P3, "pat-python", classifier: "")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("classifier").And.Contain(P3.ToString());
    }

    [Fact]
    public void Judge_SimilarityRowWithAWrongMatchNumber_Fails()
    {
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"),
                SimilarityRow(D3, "pat-dotnet", matchNumber: 0),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-python")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("matchNumber").And.Contain(D3.ToString());
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("1e400")]
    [InlineData("null")]
    [InlineData("\"0.5\"")]
    public void Judge_SimilarityScoreThatIsNotAFiniteNumber_Fails(string rawScore)
    {
        var assertions = JudgeSeeded(GoodScalarStep(), SimilarityStepWithP3Score(rawScore));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("not a finite number").And.Contain(P3.ToString());
    }

    [Fact]
    public void Judge_SimilarityScoreAbsent_Fails()
    {
        var noScore = new
        {
            matchNumber = 1,
            classifier = "A",
            data = new Dictionary<string, object?> { ["Id"] = P3.ToString(), ["Label"] = "pat-python" },
        };

        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), noScore));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("'s'");
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-1.01")]
    public void Judge_SimilarityScoreOutsideTheCosineRange_Fails(string rawScore)
    {
        var assertions = JudgeSeeded(GoodScalarStep(), SimilarityStepWithP3Score(rawScore));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain("outside [-1, 1]");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("0")]
    public void Judge_SimilarityScoreOnOrInsideTheCosineRange_Passes(string rawScore)
    {
        // Scores are never compared to absolute values — only finiteness and the cosine range.
        Similarity(JudgeSeeded(GoodScalarStep(), SimilarityStepWithP3Score(rawScore))).Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_SimilarityRowUnderAnotherLanguagesLabel_Fails()
    {
        // The set of Ids is right, but P3 came back under dotnet's partition: the client mixed up
        // which row is which.
        var assertions = JudgeSeeded(
            GoodScalarStep(),
            SimilarityStep(
                SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
                SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), SimilarityRow(P3, "pat-dotnet")));

        Similarity(assertions).Passed.Should().BeFalse();
        Similarity(assertions).Detail.Should().Contain(P3.ToString()).And.Contain("pat-python");
    }

    private static StepResult SimilarityStepWithP3Score(string rawScore)
    {
        var p3 = JsonDocument.Parse(
            $$"""{"matchNumber":1,"classifier":"A","data":{"Id":"{{P3}}","Label":"pat-python","s":""" + rawScore + "}}")
            .RootElement;

        return SimilarityStep(
            SimilarityRow(D1, "pat-dotnet"), SimilarityRow(D2, "pat-dotnet"), SimilarityRow(D3, "pat-dotnet"),
            SimilarityRow(P1, "pat-python"), SimilarityRow(P2, "pat-python"), p3);
    }

    // ── the backstop (uncited by design) ──────────────────────────────────────────────────────

    [Fact]
    public void Judge_NothingWasSeeded_FailsTheBackstopEvenThoughBothComparisonsAgree()
    {
        var assertions = MatchPatternScenario.Judge(
            "go", new Dictionary<Guid, string>(), ReadDocument(ScalarStep(), SimilarityStep()));

        Scalar(assertions).Passed.Should().BeTrue();
        Similarity(assertions).Passed.Should().BeTrue();

        var backstop = assertions.Single(a => a.Name.Contains("seeded at least one row", StringComparison.Ordinal));
        backstop.Passed.Should().BeFalse();
        backstop.RequirementId.Should().BeNull();
    }

    // ── the reporting reader ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadRows_ReadsEachRowsMatchNumberClassifierAndData()
    {
        var (rows, problem) = MatchPatternScenario.ReadRows(Rows(ScalarRow("pat-go"), SimilarityRow(D1, "pat-go")));

        problem.Should().BeNull();
        rows.Should().HaveCount(2);
        rows![0].MatchNumber.Should().Be(1);
        rows[0].Classifier.Should().Be("");
        rows[0].Data.GetProperty("first_seq").GetDouble().Should().Be(1);
        rows[1].Classifier.Should().Be("A");
    }

    [Fact]
    public void ReadRows_AnEmptyRowsArray_IsAnEmptyResultNotAMalformedOne() =>
        MatchPatternScenario.ReadRows(Rows()).Rows.Should().BeEmpty();

    [Theory]
    [InlineData("\"not an object\"", "not a JSON object")]
    [InlineData("""{"labels":[]}""", "'rows'")]
    [InlineData("""{"rows":7}""", "'rows'")]
    [InlineData("""{"rows":[7]}""", "rows[0]")]
    [InlineData("""{"rows":[{"classifier":"A","data":{}}]}""", "matchNumber")]
    [InlineData("""{"rows":[{"matchNumber":"1","classifier":"A","data":{}}]}""", "matchNumber")]
    [InlineData("""{"rows":[{"matchNumber":1,"data":{}}]}""", "classifier")]
    [InlineData("""{"rows":[{"matchNumber":1,"classifier":"A","data":[]}]}""", "data")]
    public void ReadRows_MalformedDocument_YieldsNoRowsAndNamesTheProblem(string json, string named)
    {
        var (rows, problem) = MatchPatternScenario.ReadRows(JsonDocument.Parse(json).RootElement);

        rows.Should().BeNull();
        problem.Should().Contain(named);
    }

    [Fact]
    public void ReadRows_AbsentDocument_YieldsNoRowsRatherThanThrowing() =>
        MatchPatternScenario.ReadRows(null).Rows.Should().BeNull();

    // ── the harness's own expectation ────────────────────────────────────────────────────────

    [Fact]
    public void ExpectedRows_MapsEveryKeyOfEachLanguageThatReportedAllThree_ToThatLanguage()
    {
        var keys = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["dotnet"] = new Dictionary<string, string>
            {
                ["pattern_doc_1"] = D1.ToString(), ["pattern_doc_2"] = D2.ToString(), ["pattern_doc_3"] = D3.ToString(),
            },
            // Spelled differently: parsed as UUIDs, so the five languages' spellings compare.
            ["python"] = new Dictionary<string, string>
            {
                ["pattern_doc_1"] = P1.ToString().ToUpperInvariant(),
                ["pattern_doc_2"] = P2.ToString("B"),
                ["pattern_doc_3"] = P3.ToString(),
            },
            // Only two of the three: this language did not seed its partition, so it is not
            // expected in either comparison.
            ["go"] = new Dictionary<string, string>
            {
                ["pattern_doc_1"] = Guid.NewGuid().ToString(), ["pattern_doc_2"] = Guid.NewGuid().ToString(),
            },
            // An unparsable key is the same, and must not throw.
            ["java"] = new Dictionary<string, string>
            {
                ["pattern_doc_1"] = Guid.NewGuid().ToString(), ["pattern_doc_2"] = Guid.NewGuid().ToString(),
                ["pattern_doc_3"] = "not-a-uuid",
            },
            // Keys under another scenario's logical name only.
            ["typescript"] = new Dictionary<string, string> { ["vector_doc"] = Stranger.ToString() },
        };

        MatchPatternScenario.ExpectedRows(keys).Should().BeEquivalentTo(Seeded());
    }

    [Fact]
    public void LabelFor_IsThePerLanguageIdentityTheDriversStamp() =>
        MatchPatternScenario.LabelFor("typescript").Should().Be("pat-typescript");

    [Fact]
    public void RowKeyNames_AreTheThreeLogicalKeysEveryDriverReports() =>
        MatchPatternScenario.RowKeyNames.Should().Equal("pattern_doc_1", "pattern_doc_2", "pattern_doc_3");

    // ── the projection-wait predicate ────────────────────────────────────────────────────────

    [Fact]
    public void ProjectionReady_EveryExpectedIdReturned_IsReady() =>
        MatchPatternScenario.ProjectionReady(Seeded().Keys, Seeded().Keys.ToHashSet()).Should().BeTrue();

    [Fact]
    public void ProjectionReady_OneExpectedIdNotYetReturned_IsNotReady_AndCountsItMissing()
    {
        var visible = Seeded().Keys.Where(k => k != P3).ToHashSet();

        MatchPatternScenario.ProjectionReady(Seeded().Keys, visible).Should().BeFalse();
        MatchPatternScenario.MissingFromProbe(Seeded().Keys, visible).Should().Be(1);
    }

    [Fact]
    public void ProjectionReady_ExtraIdsFromAPartiallySeededLanguage_DoNotBlockReadiness()
    {
        // The mutation this pins: an exact-count (or set-equality) predicate. A language that
        // wrote rows under the run marker but did not report all three keys is absent from the
        // expectation, yet its rows are visible to the probe — they must not stall every
        // language's wait.
        var visible = Seeded().Keys.Append(Stranger).Append(Guid.NewGuid()).ToHashSet();

        MatchPatternScenario.ProjectionReady(Seeded().Keys, visible).Should().BeTrue();
        MatchPatternScenario.MissingFromProbe(Seeded().Keys, visible).Should().Be(0);
    }

    [Fact]
    public void ProjectionReady_NothingWasSeeded_IsDeliberatelyNotReady()
    {
        // Zero expected is NOT ready on purpose: no language seeded anything, so satisfying the
        // wait would let the read phase grade five clients against nothing.
        MatchPatternScenario.ProjectionReady([], new HashSet<Guid>()).Should().BeFalse();
        MatchPatternScenario.ProjectionReady([], new HashSet<Guid> { Stranger }).Should().BeFalse();
    }

    // ── RunAsync plumbing and the read-phase grading seam ─────────────────────────────────────

    private static DriverContext Context() => new(
        Scenario: MatchPatternScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "mp-");

    private static MatchPatternScenario BuildScenario(string repoRoot = "/tmp")
    {
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost:1");
        return new MatchPatternScenario(
            new DriverRunner(repoRoot: repoRoot),
            new Reregistrar(new Iverson.Client.Contracts.ObjectMappingService.ObjectMappingServiceClient(channel)),
            new Iverson.Client.Contracts.ObjectSearchService.ObjectSearchServiceClient(channel));
    }

    private static Dictionary<string, MatchPatternScenario.LanguageState> States(params string[] languages) =>
        languages.ToDictionary(l => l, _ => new MatchPatternScenario.LanguageState(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// THE mutation this test exists for: deleting the <c>Judge</c> call inside
    /// <c>GradeReads</c>, which would leave every other test green while no QRY-005..007
    /// assertion reached a cell.
    /// </summary>
    [Fact]
    public void GradeReads_EachLanguagesJudgement_ReachesItsOwnCell()
    {
        var cells = MatchPatternScenario.GradeReads(States("dotnet", "python"),
            [
                ("dotnet", ReadDocument(GoodScalarStep(), GoodSimilarityStep())),
                ("python", ReadDocument(GoodScalarStep(), SimilarityStep())),
            ],
            Seeded());

        cells.Should().HaveCount(2);

        var dotnet = cells.Single(c => c.Language == "dotnet");
        dotnet.Status.Should().Be(CellStatus.Ok);
        dotnet.Assertions.Should().Contain(a => a.RequirementId == Requirements.QryMatchPatternReachable);
        dotnet.Assertions.Should().Contain(a => a.RequirementId == Requirements.QryMatchPatternReturnsExactlyExpectedMatches);

        var python = cells.Single(c => c.Language == "python");
        python.Status.Should().Be(CellStatus.Fail);
        python.Assertions.Should().Contain(
            a => a.RequirementId == Requirements.QryMatchPatternSimilarityScoresEveryFilteredRow && !a.Passed);
    }

    [Fact]
    public void GradeReads_ALanguageWhoseDriverReportedNoReadDocument_IsNotGreen()
    {
        var cells = MatchPatternScenario.GradeReads(States("dotnet", "go"),
            [("dotnet", ReadDocument(GoodScalarStep(), GoodSimilarityStep()))],
            Seeded());

        cells.Single(c => c.Language == "go").Status.Should().NotBe(CellStatus.Ok);
    }

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_ReturnsNoCells() =>
        (await BuildScenario().RunAsync([], Context(), "acting-token")).Should().BeEmpty();

    [Fact]
    public async Task RunAsync_TheRegisterDriverBreaks_FailsEveryRequestedLanguage()
    {
        var cells = await BuildScenario().RunAsync(["dotnet", "python"], Context(), "acting-token");

        cells.Should().HaveCount(2);
        cells.Should().NotContain(c => c.Status == CellStatus.Ok);
        cells.Should().OnlyContain(c => c.Scenario == MatchPatternScenario.Name);
    }
}
