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

    // Mutation: a locale-sensitive parse (comma-decimal) would misparse "--beta 0.003" on a
    // non-invariant machine locale; DblFlag must use CultureInfo.InvariantCulture.
    [Fact]
    public void Parse_Beta_Parses_AndDefaultsToZero()
    {
        CommandFlags.Parse(["--beta", "0.003"]).Beta.Should().Be(0.003);
        CommandFlags.Parse([]).Beta.Should().Be(0);
    }

    [Fact]
    public void Parse_HitsPath_Parses_AndDefaultsToEmpty()
    {
        CommandFlags.Parse(["--hits-path", "runs/a.chunks.hits.tsv"]).HitsPath.Should().Be("runs/a.chunks.hits.tsv");
        CommandFlags.Parse([]).HitsPath.Should().Be("");
    }
}
