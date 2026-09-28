using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Offline literal parity between <see cref="MatchPatternScenario"/> and the five conformance
/// drivers: every step name, key name, property, pattern, define and measure the orchestrator
/// grades on must appear verbatim in each driver's source, and each read step's literals must
/// appear in THAT step's builder calls. Without this, a driver that renamed a step or reworded a
/// define would only be caught by a live run.
///
/// <para>Every expected value is read from the scenario's <c>internal</c> constants, never retyped
/// here, so the orchestrator and this test share one source of truth.</para>
///
/// <para><b>Regions, not the whole file.</b> A literal found anywhere in the file proves little:
/// other scenarios in the same driver also say <c>"Marker"</c> and <c>"Seq"</c>, and the two read
/// steps share most of their vocabulary, so swapping a value between them would go unseen. Each
/// read step's literals are therefore matched only inside that step's region, bounded by three
/// per-driver markers (<see cref="Layouts"/>): scalar = [scalar start, similarity start),
/// similarity = [similarity start, similarity end). The markers are per driver because the layouts
/// genuinely differ — .NET, Go and Java name the step BEFORE its builder calls, while Python and
/// TypeScript build the request first and name the step when reporting it — so the step name alone
/// cannot bracket the builder in every language. Each marker must occur exactly once, in order,
/// and each region must also contain its own quoted step name.</para>
///
/// <para><b>Code, not comments.</b> Comments are stripped before any matching (see
/// <see cref="StripComments"/>), so a literal kept alive only in a comment does not count.</para>
///
/// <para><b>Quoted, not bare.</b> A literal is matched as a string literal — wrapped in <c>"</c> or
/// <c>'</c>, the two quote styles the drivers use — because a bare <c>Contains("n")</c> or
/// <c>Contains("s")</c> would match almost any source. The two prefixes a driver completes at
/// runtime (<see cref="MatchPatternScenario.LabelPrefix"/> and
/// <see cref="MatchPatternScenario.RowKeyPrefix"/>) are matched bare, since each language
/// concatenates or interpolates them differently.</para>
/// </summary>
public class MatchPatternDriverLiteralsTests
{
    private const string DotNet = "Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs";
    private const string Python = "Iverson.Clients/Python/conformance/driver.py";
    private const string TypeScript = "Iverson.Clients/TypeScript/conformance/driver.ts";
    private const string Go = "Iverson.Clients/Go/conformance/main.go";
    private const string Java = "Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java";

    private static readonly string[] AllDrivers = [DotNet, Python, TypeScript, Go, Java];

    public static TheoryData<string> Drivers() => new(AllDrivers);

    /// <summary>Only these drivers implement the register phase; the harness only ever asks .NET.</summary>
    public static TheoryData<string> RegisteringDrivers() => new() { DotNet, Python };

    /// <summary>
    /// Per driver: the three region markers (each unique in the comment-stripped source), then its
    /// own spelling of ONE_ROW (scalar) and ALL_ROWS_SHOW_EMPTY (similarity), including the builder
    /// call so the mode is tied to a rows-per-match call.
    /// </summary>
    private static readonly (string Driver, string ScalarStart, string SimilarityStart, string SimilarityEnd,
        string OneRow, string AllRowsShowEmpty)[] LayoutTable =
    [
        (
            DotNet,
            "Step(\"match_pattern_scalar\"",
            "Step(\"match_pattern_similarity\"",
            "async Task<JsonElement?> MatchRowsAsync(",
            ".RowsPerMatch(RowsPerMatch.OneRow)", ".RowsPerMatch(RowsPerMatch.AllRowsShowEmpty)"
        ),
        (
            // Builder first, step name when reporting: the scalar region opens at the read branch
            // and the similarity region at the scalar step's failure report, which sits between
            // the two try blocks.
            Python,
            "elif phase == \"read\" and scenario == \"match-pattern\":",
            "StepResult(\"match_pattern_scalar\", False",
            "StepResult(\"match_pattern_similarity\", False",
            ".rows_per_match(search_pb.ONE_ROW)", ".rows_per_match(search_pb.ALL_ROWS_SHOW_EMPTY)"
        ),
        (
            // Same layout as Python.
            TypeScript,
            "phase === 'read' && scenario === 'match-pattern'",
            "step('match_pattern_scalar', false",
            "step('match_pattern_similarity', false",
            ".rowsPerMatch(RowsPerMatch.ONE_ROW)", ".rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)"
        ),
        (
            // Each step closure opens with its coordinator-error return, ahead of the builder.
            Go,
            "failStep(\"match_pattern_scalar\", patCoordErr)",
            "failStep(\"match_pattern_similarity\", patCoordErr)",
            "okStep(\"match_pattern_similarity\")",
            "RowsPerMatch(pb.RowsPerMatch_ONE_ROW)", "RowsPerMatch(pb.RowsPerMatch_ALL_ROWS_SHOW_EMPTY)"
        ),
        (
            Java,
            "steps.add(step(\"match_pattern_scalar\"",
            "steps.add(step(\"match_pattern_similarity\"",
            "private static JsonElement matchRowsReport(",
            ".rowsPerMatch(RowsPerMatch.ONE_ROW)", ".rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)"
        ),
    ];

    public static TheoryData<string, string, string, string, string, string> Layouts()
    {
        var data = new TheoryData<string, string, string, string, string, string>();
        foreach (var l in LayoutTable)
            data.Add(l.Driver, l.ScalarStart, l.SimilarityStart, l.SimilarityEnd, l.OneRow, l.AllRowsShowEmpty);
        return data;
    }

    [Fact]
    public void Layouts_CoverEveryDriver() =>
        LayoutTable.Select(l => l.Driver).Should().BeEquivalentTo(AllDrivers);

    [Theory]
    [MemberData(nameof(Drivers))]
    public void EveryDriver_SendsTheScenarioWideLiterals(string driver)
    {
        var source = Read(driver);

        var missing = new[] { MatchPatternScenario.Name, MatchPatternScenario.WriteStepName }
            .Where(l => !ContainsQuoted(source, l))
            .Concat(new[] { MatchPatternScenario.LabelPrefix, MatchPatternScenario.RowKeyPrefix }
                .Where(l => !source.Contains(l, StringComparison.Ordinal)))
            .ToList();

        missing.Should().BeEmpty($"{driver} must send every match-pattern literal the orchestrator grades on");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void EveryDriversReadSteps_EachSendTheirOwnLiterals(
        string driver, string scalarStart, string similarityStart, string similarityEnd,
        string oneRow, string allRowsShowEmpty)
    {
        var source = Read(driver);
        var (scalar, similarity) = Regions(driver, source, scalarStart, similarityStart, similarityEnd);

        string[] shared =
        [
            MatchPatternScenario.MarkerProperty,
            MatchPatternScenario.PartitionProperty,
            MatchPatternScenario.SeqProperty,
        ];

        var missing = Missing("scalar step", scalar, oneRow,
            [
                .. shared,
                MatchPatternScenario.ScalarStepName,
                MatchPatternScenario.ScalarPattern,
                MatchPatternScenario.ScalarDefine,
                MatchPatternScenario.CountMeasureName,
                MatchPatternScenario.CountMeasure,
                MatchPatternScenario.FirstSeqMeasureName,
                MatchPatternScenario.FirstSeqMeasure,
                MatchPatternScenario.LastSeqMeasureName,
                MatchPatternScenario.LastSeqMeasure,
            ])
            .Concat(Missing("similarity step", similarity, allRowsShowEmpty,
            [
                .. shared,
                MatchPatternScenario.SimilarityStepName,
                MatchPatternScenario.SimilarityPattern,
                MatchPatternScenario.SimilarityDefine,
                MatchPatternScenario.SimilarityExpression,
                MatchPatternScenario.ScoreMeasureName,
            ]))
            .ToList();

        missing.Should().BeEmpty(
            $"each of {driver}'s read steps must send the orchestrator's literals in its own builder calls");
    }

    /// <summary>
    /// The drivers append Seq 1..3 to <see cref="MatchPatternScenario.RowKeyPrefix"/> at runtime, so
    /// the parity test above can only check the prefix. This pins the other half: the key names the
    /// orchestrator expects are exactly that prefix plus the three Seq values.
    /// </summary>
    [Fact]
    public void RowKeyNames_AreThePrefixFollowedBySeqOneToThree() =>
        MatchPatternScenario.RowKeyNames.Should().Equal(
            Enumerable.Range(1, 3).Select(seq => $"{MatchPatternScenario.RowKeyPrefix}{seq}"));

    [Theory]
    [MemberData(nameof(RegisteringDrivers))]
    public void EveryRegisteringDriver_ReportsTheRegisterStep(string driver) =>
        ContainsQuoted(Read(driver), MatchPatternScenario.RegisterStepName).Should().BeTrue(
            $"{driver} implements the register phase and must report step '{MatchPatternScenario.RegisterStepName}'");

    // ── the comment stripper, pinned on the cases it must get right ──────────────────────────

    [Theory]
    [InlineData("    // .pattern('A B+')", false, "")]
    [InlineData("    # .pattern(\"A B+\")", true, "")]
    [InlineData("     * {@code \"A B+\"}", false, "")]
    [InlineData("    /* \"A B+\" */", false, "")]
    [InlineData("    .pattern('A B*') // was 'A B+'", false, "    .pattern('A B*') ")]
    [InlineData("    .pattern(\"A B*\")  # was \"A B+\"", true, "    .pattern(\"A B*\")  ")]
    [InlineData("    GrpcUrl = \"http://localhost:5000\"; // the default", false, "    GrpcUrl = \"http://localhost:5000\"; ")]
    [InlineData("    .define('A', \"SIMILARITY(Title, 'x') IS NOT NULL\")", false, "    .define('A', \"SIMILARITY(Title, 'x') IS NOT NULL\")")]
    [InlineData("    label = f\"#{x}\"  # tag", true, "    label = f\"#{x}\"  ")]
    [InlineData("    s = \"a \\\" // b\" // c", false, "    s = \"a \\\" // b\" ")]
    public void StripComments_DropsCommentsAndKeepsStringLiterals(string line, bool hashComments, string expected) =>
        StripComments(line, hashComments).Should().Be(expected);

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The driver's source with its comments stripped — the only text any assertion reads.</summary>
    private static string Read(string driver)
    {
        var path = Path.Combine(RequirementsCoverageGateTests.RepositoryRoot(), driver);
        File.Exists(path).Should().BeTrue($"the {driver} conformance driver must exist");
        return StripComments(File.ReadAllText(path), hashComments: driver.EndsWith(".py", StringComparison.Ordinal));
    }

    private static (string Scalar, string Similarity) Regions(
        string driver, string source, string scalarStart, string similarityStart, string similarityEnd)
    {
        var starts = new[] { scalarStart, similarityStart, similarityEnd }
            .Select(marker =>
            {
                var first = source.IndexOf(marker, StringComparison.Ordinal);
                first.Should().BeGreaterThanOrEqualTo(0, $"{driver} must contain the region marker `{marker}`");
                source.IndexOf(marker, first + 1, StringComparison.Ordinal).Should().Be(-1,
                    $"the region marker `{marker}` must occur exactly once in {driver}, or it brackets nothing reliably");
                return first;
            })
            .ToArray();

        starts.Should().BeInAscendingOrder($"{driver}'s region markers must occur in scalar → similarity → end order");

        return (source[starts[0]..starts[1]], source[starts[1]..starts[2]]);
    }

    /// <summary>Each quoted literal, and the bare rows-per-match token, absent from <paramref name="region"/>.</summary>
    private static IEnumerable<string> Missing(
        string step, string region, string rowsPerMatch, IEnumerable<string> quotedLiterals) =>
        quotedLiterals
            .Where(literal => !ContainsQuoted(region, literal))
            .Select(literal => $"{step}: \"{literal}\"")
            .Concat(region.Contains(rowsPerMatch, StringComparison.Ordinal) ? [] : [$"{step}: {rowsPerMatch}"]);

    private static bool ContainsQuoted(string source, string literal) =>
        source.Contains($"\"{literal}\"", StringComparison.Ordinal)
        || source.Contains($"'{literal}'", StringComparison.Ordinal);

    /// <summary>
    /// Removes whole-line comments (a line whose first non-blank text is <c>//</c>, <c>/*</c>,
    /// <c>*</c> or <c>#</c>) and trailing comments (<c>//</c>, and <c>#</c> when
    /// <paramref name="hashComments"/>), where the trailing marker is only honoured OUTSIDE a
    /// string literal — so a URL's <c>//</c> or an f-string's <c>#</c> is kept. Conservative by
    /// construction: string state resets at each line end, and anything it cannot classify is
    /// kept, so it can leave comment text behind but never removes code that precedes a comment.
    /// </summary>
    internal static string StripComments(string source, bool hashComments) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith('*') || trimmed.StartsWith('#'))
            {
                return string.Empty;
            }

            char? quote = null;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quote is { } open)
                {
                    if (c == '\\') i++;
                    else if (c == open) quote = null;
                }
                else if (c is '"' or '\'' or '`')
                {
                    quote = c;
                }
                else if ((c == '/' && i + 1 < line.Length && line[i + 1] == '/') || (hashComments && c == '#'))
                {
                    return line[..i];
                }
            }

            return line;
        }));
}
