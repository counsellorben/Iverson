using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit tests for <see cref="RequirementTableParser"/> against markdown fixtures, independent of
/// the live standard document, so these stay meaningful once real requirements are added to it.
/// </summary>
public class RequirementTableParserTests
{
    [Fact]
    public void Parse_RequirementTableWithActiveAndRetiredRows_ParsesBothWithStatus()
    {
        const string markdown = """
            ### DECL — Declaration

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-DECL-001 | Active | Behaviour | Clients must do the thing. |
            | IVC-DECL-002 | Retired | Capability | Clients used to do the other thing. |
            """;

        var result = RequirementTableParser.Parse(markdown);

        result.Rows.Should().BeEquivalentTo(new[]
        {
            ("IVC-DECL-001", "Active"),
            ("IVC-DECL-002", "Retired"),
        });
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_AxisTable_IsNotMistakenForARequirementTable()
    {
        const string markdown = """
            | Axis | Name | Covers |
            | --- | --- | --- |
            | DECL | Declaration | How entity types are declared. |
            | IDN | Identity | Acting-user identity resolution. |
            """;

        var result = RequirementTableParser.Parse(markdown);

        result.Rows.Should().BeEmpty();
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EntryFormatTable_IsNotMistakenForARequirementTable()
    {
        const string markdown = """
            | Column | Meaning |
            | --- | --- |
            | ID | `IVC-<AXIS>-<NNN>`, unique across the whole document. |
            | Status | `Active` or `Retired`. |
            """;

        var result = RequirementTableParser.Parse(markdown);

        result.Rows.Should().BeEmpty();
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_IdMentionedInProse_IsNotParsedAsADeclaration()
    {
        const string markdown = """
            See IVC-DECL-001 for the corresponding declaration requirement. This document also
            cross-references IVC-REL-004 in a few places, but none of that is a table row.
            """;

        var result = RequirementTableParser.Parse(markdown);

        result.Rows.Should().BeEmpty();
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnparsableRowInsideTable_IsRecordedAsMalformed_AndDoesNotDropSubsequentRows()
    {
        // This is the Critical fix: a typo'd status ("active", lowercase) used to silently end
        // the table, which meant IVC-DECL-002 below it would vanish from the parse entirely and
        // the coverage gate would never notice it had no const and no citing assertion.
        const string markdown = """
            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-DECL-001 | active | Behaviour | Status casing is wrong on purpose. |
            | IVC-DECL-002 | Active | Behaviour | This row must still be parsed. |
            """;

        var result = RequirementTableParser.Parse(markdown);

        result.Rows.Should().ContainSingle(r => r.Id == "IVC-DECL-002" && r.Status == "Active");
        result.MalformedLines.Should().ContainSingle(l => l.Contains("IVC-DECL-001"));
    }

    [Fact]
    public void Parse_MalformedIdShapeInsideTable_IsRecordedAsMalformed_AndDoesNotDropSubsequentRows()
    {
        const string markdown = """
            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVCDECL001 | Active | Behaviour | No hyphens at all. |
            | IVC-DECL-003 | Active | Behaviour | This row must still be parsed. |
            """;

        var result = RequirementTableParser.Parse(markdown);

        result.Rows.Should().ContainSingle(r => r.Id == "IVC-DECL-003" && r.Status == "Active");
        result.MalformedLines.Should().ContainSingle(l => l.Contains("IVCDECL001"));
    }

    [Fact]
    public void Parse_BlankLine_ClosesTheTable_SoALaterTableStartsFresh()
    {
        const string markdown = """
            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-DECL-001 | Active | Behaviour | First table, first row. |

            Some prose in between, mentioning IVC-DECL-999 which must not be parsed.

            | ID | Status | Kind | Statement |
            | --- | --- | --- | --- |
            | IVC-REL-001 | Active | Behaviour | Second table, independent of the first. |
            """;

        var result = RequirementTableParser.Parse(markdown);

        result.Rows.Should().BeEquivalentTo(new[]
        {
            ("IVC-DECL-001", "Active"),
            ("IVC-REL-001", "Active"),
        });
        result.MalformedLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyMarkdown_YieldsNoRowsAndNoMalformedLines()
    {
        var result = RequirementTableParser.Parse(string.Empty);

        result.Rows.Should().BeEmpty();
        result.MalformedLines.Should().BeEmpty();
    }
}
