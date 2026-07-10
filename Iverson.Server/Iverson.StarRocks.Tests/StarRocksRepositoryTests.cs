using FluentAssertions;
using Iverson.StarRocks;
using NSubstitute;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksRepositoryTests
{
    [Fact]
    public void IStarRocksQueryExecutor_ExistsAsInterface()
    {
        var sut = Substitute.For<IEngagementStoreQueryExecutor>();
        sut.Should().NotBeNull();
    }

    [Fact]
    public void IStarRocksEntityStore_ExistsAsInterface()
    {
        var sut = Substitute.For<IEngagementStoreEntityStore>();
        sut.Should().NotBeNull();
    }

    [Fact]
    public void StarRocksRepository_ImplementsQueryAndEntityStoreRoles()
    {
        typeof(StarRocksRepository).Should().Implement<IEngagementStoreQueryExecutor>();
        typeof(StarRocksRepository).Should().Implement<IEngagementStoreEntityStore>();
    }

    [Fact]
    public void StarRocksTableSchema_StoresColumns()
    {
        var key  = new StarRocksColumnSchema("Id", "VARCHAR(36)", false);
        var cols = new List<StarRocksColumnSchema>
        {
            new("Name", "STRING", false),
            new("Bio",  "STRING", true)
        };
        var schema = new StarRocksTableSchema("authors", key, cols);

        schema.TableName.Should().Be("authors");
        schema.KeyColumn.Name.Should().Be("Id");
        schema.Columns.Should().HaveCount(2);
    }

    [Fact]
    public void AggregationDescriptor_DefaultSizeIsTen()
    {
        var spec = new AggregationDescriptor("n", AggregationKind.Terms, "Name");
        spec.Size.Should().Be(10);
    }
}
