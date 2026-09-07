using FluentAssertions;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class CommandFlagsTests
{
    // Mutation: a default of "document", or a misspelt flag name, would change every flag-less arm's meaning.
    [Fact]
    public void Parse_RerankInput_ParsesDocument_AndDefaultsToWinningChunk()
    {
        CommandFlags.Parse(["--rerank-input", "document"]).RerankInput.Should().Be("document");
        CommandFlags.Parse([]).RerankInput.Should().Be("winning-chunk");
    }

    // Mutation: a wrong default here silently changes every flag-less benchmark-query run's chunk
    // top_k (DocumentBudget * ChunkBudgetMultiplier) without anyone passing the flag at all.
    [Fact]
    public void Parse_ChunkBudgetMultiplier_Parses_AndDefaultsToFive()
    {
        CommandFlags.Parse(["--chunk-budget-multiplier", "11"]).ChunkBudgetMultiplier.Should().Be(11);
        CommandFlags.Parse([]).ChunkBudgetMultiplier.Should().Be(5);
    }
}
