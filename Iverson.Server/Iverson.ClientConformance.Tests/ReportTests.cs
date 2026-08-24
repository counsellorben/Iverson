using FluentAssertions;
using Iverson.ClientConformance;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class ReportTests
{
    // ── AllPassed: which statuses are allowed to sink a run ──────────────────────────────────
    //
    // AllPassed decides the process exit code (via RunSucceeded), and until now nothing tested it
    // — RunSucceeded was covered as a pure function, but the property that computes over cells was
    // not. That gap is exactly what makes adding a new CellStatus dangerous: a status missing from
    // the exclusion set silently makes every cell carrying it fail the whole run, and no unit test
    // would notice. These three tests are that guard; each names the mutation it kills.

    /// <summary>
    /// The mutation: dropping Skip from the exclusion set. A language whose toolchain is absent
    /// must not fail the run — skip means "not observed", never "failed".
    /// </summary>
    [Fact]
    public void AllPassed_ASkippedCell_DoesNotSinkTheRun()
    {
        var report = new Report();
        report.Add(ReportCell.Ok("dotnet", "s1", []));
        report.Add(ReportCell.Skip("java", "s1", "no toolchain"));

        report.AllPassed.Should().BeTrue();
    }

    /// <summary>
    /// The mutation: adding NotApplicable to the enum without adding it to AllPassed's exclusion
    /// set. Eight cells in a real full matrix carry it (nav-property-rejected and tenant-rejected
    /// across the four non-canonical languages), so the omission would flip a green run to exit 1
    /// while every grid cell still read fine.
    /// </summary>
    [Fact]
    public void AllPassed_ANotApplicableCell_DoesNotSinkTheRun()
    {
        var report = new Report();
        report.Add(ReportCell.Ok("dotnet", "s1", []));
        report.Add(ReportCell.NotApplicable("java", "s1", "runs once; see the 'dotnet' column"));

        report.AllPassed.Should().BeTrue();
    }

    /// <summary>
    /// The mutation in the other direction: excluding so much that a real failure stops counting.
    /// </summary>
    [Fact]
    public void AllPassed_AFailedCell_SinksTheRun()
    {
        var report = new Report();
        report.Add(ReportCell.NotApplicable("java", "s1", "runs once; see the 'dotnet' column"));
        report.Add(ReportCell.Fail("dotnet", "s1", "boom", []));

        report.AllPassed.Should().BeFalse();
    }

    /// <summary>
    /// An n/a cell renders as `n/a`, not `skip`, and its reason is still listed underneath the
    /// grid — the reason is the whole point: it names the column carrying the real result.
    /// </summary>
    [Fact]
    public void RenderText_ANotApplicableCell_RendersAsNotApplicableAndListsItsReason()
    {
        var report = new Report();
        report.Add(ReportCell.Ok("dotnet", "s1", []));
        report.Add(ReportCell.NotApplicable("java", "s1", "runs once; see the 'dotnet' column"));

        var text = report.RenderText();

        text.Should().Contain("n/a");
        text.Should().Contain("java/s1 n/a: runs once; see the 'dotnet' column");
    }

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
