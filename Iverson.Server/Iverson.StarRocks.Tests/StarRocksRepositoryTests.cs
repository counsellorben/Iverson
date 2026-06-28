using FluentAssertions;
using Iverson.StarRocks;
using NSubstitute;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksRepositoryTests
{
    [Fact]
    public void IStarRocksRepository_ExistsAsInterface()
    {
        // Verifies the interface can be substituted — used by all consumer/service tests
        var sut = Substitute.For<IStarRocksRepository>();
        sut.Should().NotBeNull();
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

    [Fact]
    public void BuildCreateTableDdl_EmitsUniqueKey_NotPrimaryKey()
    {
        var schema = new StarRocksTableSchema(
            "articles",
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Title", "STRING", false)]);

        var ddl = StarRocksRepository.BuildCreateTableDdl(schema);

        ddl.Should().Contain("UNIQUE KEY(`Id`)");
        ddl.Should().NotContain("PRIMARY KEY");
        ddl.Should().Contain("CREATE TABLE IF NOT EXISTS `articles`");
        ddl.Should().Contain("`Id` VARCHAR(36) NOT NULL");
        ddl.Should().Contain("`Title` STRING NOT NULL");
    }

    [Fact]
    public void BuildCreateTableDdl_NullableColumn_OmitsNotNull()
    {
        var schema = new StarRocksTableSchema(
            "authors",
            new StarRocksColumnSchema("Id",  "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Bio", "STRING",     true)]);

        var ddl = StarRocksRepository.BuildCreateTableDdl(schema);

        ddl.Should().Contain("`Bio` STRING\n");
        ddl.Should().NotContain("`Bio` STRING NOT NULL");
    }

    [Fact]
    public void BuildCreateMvDdl_ReturnsDdl_WhenMvSortKeyIsPopulated()
    {
        var schema = new StarRocksTableSchema(
            "articles",
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [
                new StarRocksColumnSchema("Category",    "STRING",   false),
                new StarRocksColumnSchema("PublishedAt", "DATETIME", false),
                new StarRocksColumnSchema("Body",        "STRING",   false),
            ])
        {
            MvSortKey         = ["Category", "PublishedAt"],
            MvExcludedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Body" }
        };

        var ddl = StarRocksRepository.BuildCreateMvDdl(schema);

        ddl.Should().NotBeNull();
        ddl!.Should().Contain("CREATE MATERIALIZED VIEW IF NOT EXISTS `articles_search_mv`");
        ddl.Should().Contain("`Id`");
        ddl.Should().Contain("`Category`");
        ddl.Should().Contain("`PublishedAt`");
        ddl.Should().NotContain("`Body`");
        ddl.Should().Contain("GROUP BY `Category`, `PublishedAt`");
    }

    [Fact]
    public void BuildCreateMvDdl_ReturnsNull_WhenNoMvSortKey()
    {
        var schema = new StarRocksTableSchema(
            "authors",
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Name", "STRING", false)]);

        var ddl = StarRocksRepository.BuildCreateMvDdl(schema);

        ddl.Should().BeNull();
    }
}
