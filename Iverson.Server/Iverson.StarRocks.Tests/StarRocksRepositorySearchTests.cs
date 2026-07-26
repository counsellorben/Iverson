using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class EngagementRepositorySearchTests
{
    [Fact]
    public async Task AggregateAsync_MultiKeyGroupByFields_ThrowsEngagementQueryTranslationException()
    {
        var repo = new EngagementRepository(
            "Server=localhost;Port=1;Database=x;Uid=x;Pwd=x;",
            NullLogger<EngagementRepository>.Instance);

        var schema = new EngagementQuerySchema("Article", "articles", "Id", ["Title"]);
        var spec = new AggregationDescriptor("by_title", AggregationKind.Terms, "Title",
            GroupByFields: ["Title", "Category"]);

        var act = async () => await repo.AggregateAsync(schema, null, spec);

        await act.Should().ThrowAsync<EngagementQueryTranslationException>()
            .WithMessage("*Multi-key GROUP BY*");
    }
}
