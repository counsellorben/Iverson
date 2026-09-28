using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Offline literal parity between <see cref="MatchPatternScenario"/> and the five conformance
/// drivers: every step name, key name, property, pattern, define and measure the orchestrator
/// grades on must appear verbatim in each driver's source. Without this, a driver that renamed a
/// step or reworded a define would only be caught by a live run.
///
/// <para>Every expected value is read from the scenario's <c>internal</c> constants, never retyped
/// here, so the orchestrator and this test share one source of truth.</para>
///
/// <para><b>Quoted, not bare.</b> A literal is matched as a string literal — wrapped in <c>"</c> or
/// <c>'</c>, the two quote styles the drivers use — because a bare <c>Contains("n")</c> or
/// <c>Contains("s")</c> would match almost any source file. The two prefixes a driver completes at
/// runtime (<see cref="MatchPatternScenario.LabelPrefix"/> and
/// <see cref="MatchPatternScenario.RowKeyPrefix"/>) are matched bare, since each language
/// concatenates or interpolates them differently.</para>
/// </summary>
public class MatchPatternDriverLiteralsTests
{
    public static TheoryData<string> Drivers() => new()
    {
        "Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs",
        "Iverson.Clients/Python/conformance/driver.py",
        "Iverson.Clients/TypeScript/conformance/driver.ts",
        "Iverson.Clients/Go/conformance/main.go",
        "Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java",
    };

    /// <summary>Only these drivers implement the register phase; the harness only ever asks .NET.</summary>
    public static TheoryData<string> RegisteringDrivers() => new()
    {
        "Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs",
        "Iverson.Clients/Python/conformance/driver.py",
    };

    /// <summary>
    /// Each driver's own spelling of ONE_ROW (the scalar step) and ALL_ROWS_SHOW_EMPTY (the
    /// similarity step), including the builder call, so the mode is tied to a rows-per-match call.
    /// </summary>
    public static TheoryData<string, string, string> RowsPerMatchSpellings() => new()
    {
        {
            "Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Program.cs",
            ".RowsPerMatch(RowsPerMatch.OneRow)", ".RowsPerMatch(RowsPerMatch.AllRowsShowEmpty)"
        },
        {
            "Iverson.Clients/Python/conformance/driver.py",
            ".rows_per_match(search_pb.ONE_ROW)", ".rows_per_match(search_pb.ALL_ROWS_SHOW_EMPTY)"
        },
        {
            "Iverson.Clients/TypeScript/conformance/driver.ts",
            ".rowsPerMatch(RowsPerMatch.ONE_ROW)", ".rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)"
        },
        {
            "Iverson.Clients/Go/conformance/main.go",
            "RowsPerMatch(pb.RowsPerMatch_ONE_ROW)", "RowsPerMatch(pb.RowsPerMatch_ALL_ROWS_SHOW_EMPTY)"
        },
        {
            "Iverson.Clients/Java/conformance/src/main/java/io/iverson/conformance/Driver.java",
            ".rowsPerMatch(RowsPerMatch.ONE_ROW)", ".rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)"
        },
    };

    [Theory]
    [MemberData(nameof(Drivers))]
    public void EveryDriver_SendsTheOrchestratorsLiterals(string driver)
    {
        var source = Read(driver);

        string[] quoted =
        [
            MatchPatternScenario.Name,
            MatchPatternScenario.WriteStepName,
            MatchPatternScenario.ScalarStepName,
            MatchPatternScenario.SimilarityStepName,
            MatchPatternScenario.MarkerProperty,
            MatchPatternScenario.SeqProperty,
            MatchPatternScenario.ScalarPattern,
            MatchPatternScenario.ScalarDefine,
            MatchPatternScenario.CountMeasureName,
            MatchPatternScenario.CountMeasure,
            MatchPatternScenario.FirstSeqMeasureName,
            MatchPatternScenario.FirstSeqMeasure,
            MatchPatternScenario.LastSeqMeasureName,
            MatchPatternScenario.LastSeqMeasure,
            MatchPatternScenario.SimilarityPattern,
            MatchPatternScenario.SimilarityDefine,
            MatchPatternScenario.SimilarityExpression,
            MatchPatternScenario.ScoreMeasureName,
        ];

        var missing = quoted.Where(l => !ContainsQuoted(source, l))
            .Concat(new[] { MatchPatternScenario.LabelPrefix, MatchPatternScenario.RowKeyPrefix }
                .Where(l => !source.Contains(l, StringComparison.Ordinal)))
            .ToList();

        missing.Should().BeEmpty($"{driver} must send every match-pattern literal the orchestrator grades on");
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

    [Theory]
    [MemberData(nameof(RowsPerMatchSpellings))]
    public void EveryDriver_SpellsBothRowsPerMatchModes(string driver, string oneRow, string allRowsShowEmpty)
    {
        var source = Read(driver);

        source.Should().Contain(oneRow, $"{driver}'s scalar step must request ONE_ROW");
        source.Should().Contain(allRowsShowEmpty, $"{driver}'s similarity step must request ALL_ROWS_SHOW_EMPTY");
    }

    private static string Read(string driver)
    {
        var path = Path.Combine(RequirementsCoverageGateTests.RepositoryRoot(), driver);
        File.Exists(path).Should().BeTrue($"the {driver} conformance driver must exist");
        return File.ReadAllText(path);
    }

    private static bool ContainsQuoted(string source, string literal) =>
        source.Contains($"\"{literal}\"", StringComparison.Ordinal)
        || source.Contains($"'{literal}'", StringComparison.Ordinal);
}
