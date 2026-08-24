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
    public void Parse_DeferredRowWithEmptyEvidenceCell_ParsesAsAWellFormedRow_NotMalformed()
    {
        // A well-formed three-cell row with an empty third cell is a gate-level defect (mode 4 —
        // a Deferred area with an empty reason), not a structurally malformed one (mode 6). The
        // parser must let it through so the gate check can name the axis and area.
        const string markdown = """
            ### REG — Registration

            #### Coverage

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Reregistration | Deferred |  |
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().BeEquivalentTo(new[]
        {
            new CoverageTableParser.CoverageRow("REG", "Reregistration", "Deferred", string.Empty),
        });
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_PipeTableUnderNonCoverageHeading_IsIgnored()
    {
        // A `| Area | Status | Evidence |`-headed table that is not preceded by a `#### Coverage`
        // heading (e.g. an illustrative example under a prose subsection) must not be bound as a
        // real ledger — otherwise a future axis's authoring-notes example could silently satisfy
        // the axis-completeness check for real requirement IDs it happens to cite.
        const string markdown = """
            ### REL — Relations

            #### Authoring notes (for future axes)

            | Area | Status | Evidence |
            | --- | --- | --- |
            | Example area | Covered | IVC-REL-001 |
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().BeEmpty();
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_PipeTableInsideFencedCodeBlock_IsIgnored()
    {
        // A `#### Coverage` heading followed by a fenced code block containing a pipe table (e.g.
        // a documentation example of the table shape) must not be bound as a real ledger.
        const string markdown = """
            ### REL — Relations

            #### Coverage

            ```
            | Area | Status | Evidence |
            | --- | --- | --- |
            | Example area | Covered | IVC-REL-001 |
            ```
            """;

        var result = CoverageTableParser.Parse(markdown, KnownAxes);

        result.Rows.Should().BeEmpty();
        result.MalformedLines.Should().BeEmpty();
    }
}
