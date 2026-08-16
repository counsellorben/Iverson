using FluentAssertions;
using Iverson.ClientConformance;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class ReportTests
{
    // The matrix column-attribution bug: a fixed width of 8 is narrower than "typescript" (10
    // chars), so every column at or after it in render order drifts two characters right of its
    // header. Column widths must be derived from the longest language name actually being
    // rendered, not a constant.

    [Fact]
    public void RenderText_LanguageNameLongerThanEightChars_HeaderAndDataCellsStayAligned()
    {
        var report = new Report();
        report.Add(ReportCell.Ok("dotnet", "s1", []));
        report.Add(ReportCell.Ok("typescript", "s1", []));
        report.Add(ReportCell.Fail("go", "s1", "boom", []));

        var lines = report.RenderText()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var headerLine = lines[0];
        var dataLine = lines[1];

        var typescriptHeaderIndex = headerLine.IndexOf("typescript", StringComparison.Ordinal);
        var goHeaderIndex = headerLine.IndexOf("go", StringComparison.Ordinal);

        // "go"'s data cell ("FAIL") must start at the same column as "go"'s header label, and that
        // column must come after typescript's — not two characters further right than it should,
        // which is what a fixed width of 8 produces for a 10-character language name ahead of it.
        var goDataIndex = dataLine.IndexOf("FAIL", StringComparison.Ordinal);
        goDataIndex.Should().Be(goHeaderIndex);
        goHeaderIndex.Should().BeGreaterThan(typescriptHeaderIndex);
    }
}
