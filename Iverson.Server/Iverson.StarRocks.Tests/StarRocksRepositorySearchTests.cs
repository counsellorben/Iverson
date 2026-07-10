using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksRepositorySearchTests
{
    [Fact]
    public async Task AggregateAsync_MultiKeyGroupByFields_ThrowsStarRocksQueryTranslationException()
    {
        var repo = new StarRocksRepository(
            "Server=localhost;Port=1;Database=x;Uid=x;Pwd=x;",
            NullLogger<StarRocksRepository>.Instance);

        var schema = new StarRocksQuerySchema("Article", "articles", "Id", ["Title"]);
        var spec = new AggregationDescriptor("by_title", AggregationKind.Terms, "Title",
            GroupByFields: ["Title", "Category"]);

        var act = async () => await repo.AggregateAsync(schema, null, spec);

        await act.Should().ThrowAsync<StarRocksQueryTranslationException>()
            .WithMessage("*Multi-key GROUP BY*");
    }
}
