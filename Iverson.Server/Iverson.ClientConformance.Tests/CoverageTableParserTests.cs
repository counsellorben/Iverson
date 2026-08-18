using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit tests for <see cref="CoverageTableParser"/> against markdown fixtures, independent of the
/// live standard document, so these stay meaningful once real coverage ledgers are added to it.
/// </summary>
public class CoverageTableParserTests
{
    private static readonly string[] KnownAxes = { "DECL", "REL", "REG", "LIFE" };

    [Fact]
    public void Parse_WellFormedTable_UnderAxisHeading_ParsesRowsAttributedToThatAxis()
    {
        const string markdown = """
            ### DECL — Declaration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-DECL-001 | Active | Behaviour | Clients must do the thing. |

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Key property declaration | Covered | IVC-DECL-001 |
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().BeEquivalentTo(new[]
        {
            new CoverageTableParser.CoverageRow("DECL", "Key property declaration", "Covered", "IVC-DECL-001"),
        });
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_TwoTablesUnderDifferentHeadings_AreAttributedToTheCorrectAxis()
    {
        const string markdown = """
            ### DECL — Declaration

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Key property declaration | Covered | IVC-DECL-001 |

            ### REL — Relations

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Foreign-key synthesis | Covered | IVC-REL-001 |
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().BeEquivalentTo(new[]
        {
            new CoverageTableParser.CoverageRow("DECL", "Key property declaration", "Covered", "IVC-DECL-001"),
            new CoverageTableParser.CoverageRow("REL", "Foreign-key synthesis", "Covered", "IVC-REL-001"),
        });
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnparsableRowInsideTable_IsRecordedAsMalformed_AndDoesNotDropSubsequentRows()
    {
        const string markdown = """
            ### DECL — Declaration

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Only two columns | Covered |
            | Key property declaration | Covered | IVC-DECL-001 |
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().ContainSingle(r => r.Area == "Key property declaration");
        result.MalformedLines.Should().ContainSingle(l => l.Contains("Only two columns"));
    }

    [Fact]
    public void Parse_BlankLine_ClosesTheTable_SoALaterTableStartsFresh()
    {
        const string markdown = """
            ### DECL — Declaration

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Key property declaration | Covered | IVC-DECL-001 |

            Some prose in between.

            ### REL — Relations

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Foreign-key synthesis | Covered | IVC-REL-001 |
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().BeEquivalentTo(new[]
        {
            new CoverageTableParser.CoverageRow("DECL", "Key property declaration", "Covered", "IVC-DECL-001"),
            new CoverageTableParser.CoverageRow("REL", "Foreign-key synthesis", "Covered", "IVC-REL-001"),
        });
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_CoverageTable_UnderNonAxisHeading_IsAttributedToNoAxis()
    {
        const string markdown = """
            ### Scope: behaviour and capability only

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Stray table | Covered | IVC-DECL-001 |
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().ContainSingle();
        result.Rows[0].Axis.Should().BeNull();
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_RequirementTable_IsNotParsedAsACoverageTable()
    {
        const string markdown = """
            ### DECL — Declaration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-DECL-001 | Active | Behaviour | Clients must do the thing. |
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().BeEmpty();
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyMarkdown_YieldsNoRowsAndNoMalformedLines()
    {
        var result = CoverageTableParser.Parse(string.Empty, KnownAxes);

        result.Rows.Should().BeEmpty();
        result.MalformedLines.Should().BeEmpty();
    }
}
