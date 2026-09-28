using FluentAssertions;
using Iverson.Api.Grpc;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public sealed class PatternQueryLimitOptionsTests
{
    private static readonly string[] Keys =
    [
        "MaxPatternLength", "MaxProgramInstructions", "MaxExpressionLength", "MaxDefines", "MaxMeasures", "MaxSubsets",
        "MaxSimilarityTerms", "MaxOutputRows", "MaxRowsScanned", "MaxPartitionRows", "MaxActiveThreads", "MaxSteps",
        "TimeoutSeconds", "BatchRows",
    ];

    public static TheoryData<string> EveryKey() => new(Keys);

    /// <summary>The defaults with <paramref name="key"/> set to <paramref name="value"/>.</summary>
    private static PatternQueryLimitOptions With(string key, int value)
    {
        var d = PatternQueryLimitOptions.Default;
        return new PatternQueryLimitOptions
        {
            MaxPatternLength = key == "MaxPatternLength" ? value : d.MaxPatternLength,
            MaxProgramInstructions = key == "MaxProgramInstructions" ? value : d.MaxProgramInstructions,
            MaxExpressionLength = key == "MaxExpressionLength" ? value : d.MaxExpressionLength,
            MaxDefines = key == "MaxDefines" ? value : d.MaxDefines,
            MaxMeasures = key == "MaxMeasures" ? value : d.MaxMeasures,
            MaxSubsets = key == "MaxSubsets" ? value : d.MaxSubsets,
            MaxSimilarityTerms = key == "MaxSimilarityTerms" ? value : d.MaxSimilarityTerms,
            MaxOutputRows = key == "MaxOutputRows" ? value : d.MaxOutputRows,
            MaxRowsScanned = key == "MaxRowsScanned" ? value : d.MaxRowsScanned,
            MaxPartitionRows = key == "MaxPartitionRows" ? value : d.MaxPartitionRows,
            MaxActiveThreads = key == "MaxActiveThreads" ? value : d.MaxActiveThreads,
            MaxSteps = key == "MaxSteps" ? value : d.MaxSteps,
            TimeoutSeconds = key == "TimeoutSeconds" ? value : d.TimeoutSeconds,
            BatchRows = key == "BatchRows" ? value : d.BatchRows,
        };
    }

    [Fact]
    public void The_defaults_are_valid() => PatternQueryLimitOptions.Default.Invoking(o => o.Validate()).Should().NotThrow();

    [Theory]
    [MemberData(nameof(EveryKey))]
    public void A_zero_or_negative_limit_is_rejected_by_name(string key)
    {
        foreach (var value in new[] { 0, -1 })
        {
            var message = With(key, value).Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().Which.Message;

            message.Should().Contain($"Patterns:Limits:{key} ");
            Keys.Where(k => k != key).Should().OnlyContain(k => !message.Contains($"Patterns:Limits:{k} "));
        }
        With(key, 1).Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Every_invalid_key_is_named_in_one_exception()
    {
        var all = new PatternQueryLimitOptions
        {
            MaxPatternLength = 0, MaxProgramInstructions = 0, MaxExpressionLength = 0, MaxDefines = 0, MaxMeasures = 0,
            MaxSubsets = 0, MaxSimilarityTerms = 0, MaxOutputRows = 0, MaxRowsScanned = 0, MaxPartitionRows = 0,
            MaxActiveThreads = 0, MaxSteps = 0, TimeoutSeconds = 0, BatchRows = 0,
        };

        var message = all.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().Which.Message;

        Keys.Should().OnlyContain(k => message.Contains($"Patterns:Limits:{k} "));
    }

    [Fact]
    public void MaxRowsScanned_must_leave_room_for_the_one_extra_row_the_read_asks_for()
    {
        With("MaxRowsScanned", int.MaxValue).Invoking(o => o.Validate())
            .Should().Throw<InvalidOperationException>().WithMessage("*Patterns:Limits:MaxRowsScanned *");
        With("MaxRowsScanned", int.MaxValue - 1).Invoking(o => o.Validate()).Should().NotThrow();
    }
}
