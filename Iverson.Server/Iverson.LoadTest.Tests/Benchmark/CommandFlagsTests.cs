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
}
